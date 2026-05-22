// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    /// <summary>
    /// Resolves a Swift type to its C# name for proxy context.
    /// Delegates to the consolidated <see cref="ProtocolSignatureHelper.ProjectTypeToCSharp"/>
    /// with proxy-specific mode flags (ExistentialFallback + IncludeTupleLabels).
    /// When <paramref name="forAbiMarshalling"/> is true, returns the ABI type
    /// (e.g., Swift.SwiftString, ExistentialContainer0) suitable for MarshalFromSwift&lt;T&gt;.
    /// When false (default), returns the idiomatic C# type (e.g., string, bool?).
    /// </summary>
    private string GetCSharpTypeName(TypeSpec? typeSpec, bool forAbiMarshalling = false, bool isParameter = true)
    {
        if (typeSpec == null) return "object";

        var mode = TypeResolutionMode.ExistentialFallback | TypeResolutionMode.IncludeTupleLabels;
        if (forAbiMarshalling)
            mode |= TypeResolutionMode.AbiMarshalling;

        return ProtocolSignatureHelper.ProjectTypeToCSharp(
            typeSpec, _typeDatabase, protocolContext: null, isParameter: isParameter,
            genericContext: GenericContext.Empty, mode: mode);
    }

    /// <summary>
    /// Resolves property types to match the emitted interface signatures from ProtocolHandler.
    /// Delegates to the consolidated <see cref="ProtocolSignatureHelper.ProjectTypeToCSharp"/>
    /// with NativeInt narrowing and the property's parent generic context.
    /// </summary>
    private string GetInterfaceCompatiblePropertyTypeName(PropertyDecl property)
    {
        var propGenericContext = property.ParentDecl is TypeDecl propParentType && propParentType.IsGeneric
            ? GenericContext.FromType(propParentType)
            : GenericContext.Empty;

        return ProtocolSignatureHelper.ProjectTypeToCSharp(
            property.SwiftTypeSpec, _typeDatabase, protocolContext: null, isParameter: false,
            genericContext: propGenericContext, mode: TypeResolutionMode.NarrowNativeInt);
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

    /// <summary>
    /// Returns the per-decl scaffolding prefix used to disambiguate cross-module
    /// parent emissions inside the child proxy's class body. Same-module decls
    /// get an empty prefix so the existing struct/symbol names stay unchanged.
    /// Cross-module decls get a <c>{Module}_</c> prefix so two parents with the
    /// same simple name from different dependency modules emit distinct C#
    /// struct names (otherwise CS0102 — type already contains a definition for —
    /// fires when both <c>private struct ParentDelegateSwiftVTable</c> appear in
    /// the same child proxy class).
    /// </summary>
    private string GetVtableNameModulePrefix(ProtocolDecl protocolDecl)
    {
        var sourceModule = protocolDecl.ModuleDecl?.Name;
        if (string.IsNullOrEmpty(sourceModule) || sourceModule == _moduleName)
            return string.Empty;
        return sourceModule + "_";
    }

    private string GetSwiftVtableStructName(ProtocolDecl protocolDecl)
    {
        return $"{GetVtableNameModulePrefix(protocolDecl)}{protocolDecl.Name}SwiftVTable";
    }

    private string GetLocalVtableStructName(ProtocolDecl protocolDecl)
    {
        return $"{GetVtableNameModulePrefix(protocolDecl)}{protocolDecl.Name}LocalVTable";
    }

    private string GetSetVtablePInvokeName(ProtocolDecl protocolDecl)
    {
        return $"Set{GetVtableNameModulePrefix(protocolDecl)}{protocolDecl.Name}_vtable";
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
