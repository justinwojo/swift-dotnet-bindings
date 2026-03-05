// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    /// <summary>
    /// Resolves a Swift type to its C# name. Uses TypeProjectionFactory for public type
    /// resolution (!forAbiMarshalling) with fallbacks for closures, existentials, and bound generics.
    /// When <paramref name="forAbiMarshalling"/> is true, returns the ABI type
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

        // Factory-first path for type resolution.
        // When forAbiMarshalling=true, use MarshalFromSwiftType — the type suitable for
        // MarshalFromSwift<T> deserialization. This composes correctly through containers:
        //   - Existentials: ExistentialContainer1 (not AnyType)
        //   - Classes/NonFrozenStructs: public type name (not IntPtr)
        //   - Strings: SwiftString (ABI type)
        //   - Arrays/Dicts/Optionals: compose inner MarshalFromSwiftType recursively
        // When forAbiMarshalling=false, use PublicType (e.g., IReadOnlyList<ISQLSelectable>)
        {
            var factory = new TypeProjectionFactory();
            var projection = factory.Project(typeSpec, new ProjectionContext
            {
                TypeDatabase = _typeDatabase,
                IsParameter = isParameter,
                GenericContext = GenericContext.Empty
            });
            if (projection != null)
                return forAbiMarshalling ? projection.MarshalFromSwiftType : projection.PublicType;
        }

        // Factory fallback for unsupported types
        if (typeSpec is ClosureTypeSpec closureTypeSpec)
            return GetClosureCSharpType(closureTypeSpec);

        if (typeSpec is TupleTypeSpec tupleTypeSpec)
        {
            if (tupleTypeSpec.IsEmptyTuple)
                return "void";
            return GetTupleCSharpType(tupleTypeSpec);
        }

        // Existentials: ABI → container type, public → interface/well-known
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
        }

        // Bound generic fallback: produce full type name with generic args
        // (e.g., BatchedCollection<Swift.AnyType> for unknown inner types)
        if (typeSpec is NamedTypeSpec proxyBoundGeneric && proxyBoundGeneric.ContainsGenericParameters)
        {
            var bgh = new BoundGenericsHandler(_typeDatabase);
            return bgh.TranslateBoundGenericTypeToCSharp((TypeSpec)proxyBoundGeneric, GenericContext.Empty);
        }

        // Type record fallback
        var record = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
        return record.CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Resolves property types to match the emitted interface signatures from ProtocolHandler.
    /// Uses the same resolution rules so proxy signatures always match interface declarations.
    /// </summary>
    private string GetInterfaceCompatiblePropertyTypeName(PropertyDecl property)
    {
        // Factory with GenericContext for all types including bound generics
        var propGenericContext = property.ParentDecl is TypeDecl propParentType && propParentType.IsGeneric
            ? GenericContext.FromType(propParentType)
            : GenericContext.Empty;
        var propFactory = new TypeProjectionFactory();
        var propProjection = propFactory.Project(property.SwiftTypeSpec, new ProjectionContext
        {
            TypeDatabase = _typeDatabase,
            IsParameter = false,
            GenericContext = propGenericContext
        });
        if (propProjection != null)
            return NativeIntOverloadEmitter.NarrowNativeIntType(propProjection.PublicType);

        // Bound generic fallback: produce full type name with generic args
        if (property.SwiftTypeSpec is NamedTypeSpec propBoundGeneric && propBoundGeneric.ContainsGenericParameters)
        {
            var bgh = new BoundGenericsHandler(_typeDatabase);
            return NativeIntOverloadEmitter.NarrowNativeIntType(
                bgh.TranslateBoundGenericTypeToCSharp(property.SwiftTypeSpec, propGenericContext));
        }

        return NativeIntOverloadEmitter.NarrowNativeIntType(
            _typeDatabase.GetTypeRecordOrAnyType(property.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName);
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
    /// For nested protocols (declared inside a class/struct), the interface name is qualified
    /// with the parent type name so the proxy class (emitted at module level) can find it.
    /// </summary>
    private static string GetInterfaceNameWithGenerics(ProtocolDecl protocolDecl)
    {
        var baseName = NameProvider.GetInterfaceName(protocolDecl.Name, moduleName: protocolDecl.ModuleDecl?.Name ?? "");

        // For nested protocols, qualify with parent type name(s).
        // The proxy class is emitted at module level, so it needs the full path
        // (e.g., CountryCodePickerViewController.ICountryCodePickerTableViewCellProtocol).
        if (protocolDecl.ParentDecl is TypeDecl parentType)
        {
            var parentNames = new List<string>();
            BaseDecl? current = parentType;
            while (current is TypeDecl td)
            {
                parentNames.Insert(0, td.Name);
                current = td.ParentDecl;
            }
            baseName = string.Join(".", parentNames) + "." + baseName;
        }

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
