// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared type classification and marshalling logic for extension emitters
/// (ForeignTypeExtensionEmitter and CrossModuleExtensionEmitter).
///
/// Extracted from duplicated classify/marshalling code in both emitters.
/// ProtocolExtensionEmitter does NOT use this — it delegates to the standard
/// MethodHandler pipeline via synthetic MethodDecl injection.
/// </summary>
public static class ExtensionMarshallingHelper
{
    /// <summary>
    /// Categorizes return types for correct C# marshalling in extension methods.
    /// </summary>
    public enum ReturnKind
    {
        Void,
        Primitive,
        ObjCClass,
        SwiftClass,
        NonFrozenStruct,
        FrozenStruct,
    }

    /// <summary>
    /// Categorizes parameter types for correct C# marshalling in extension methods.
    /// </summary>
    public enum ParamKind
    {
        Primitive,
        ObjCClass,
        SwiftClass,
        SimpleEnum,
        FrozenStruct,
    }

    /// <summary>
    /// Classifies a return TypeSpec into a ReturnKind for marshalling.
    /// Returns null if the type is not supported.
    /// Union of ForeignTypeExtensionEmitter + CrossModuleExtensionEmitter logic.
    /// </summary>
    public static ReturnKind? ClassifyReturnType(TypeSpec? typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec == null)
            return ReturnKind.Void;

        if (typeSpec is TupleTypeSpec tuple && tuple.IsEmptyTuple)
            return ReturnKind.Void;

        if (typeSpec is not NamedTypeSpec namedType)
            return null;

        if (namedType.ContainsGenericParameters)
            return null;

        if (MarshallingHelpers.IsSwiftPrimitive(namedType.Name))
            return ReturnKind.Primitive;

        if (MarshallingHelpers.TypeAliasToCSPrimitive.ContainsKey(namedType.Name))
            return ReturnKind.Primitive;

        try
        {
            if (TypeDatabaseExtensions.IsObjCModuleType(namedType))
                return ReturnKind.ObjCClass;

            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            {
                // ObjC-rooted classes (Swift classes inheriting NSObject) use ObjC bridge marshalling
                if (typeRecord.Kind == TypeRecordKind.Class && MarshallingHelpers.IsObjCRooted(typeRecord))
                    return ReturnKind.ObjCClass;
                if (typeRecord.Kind == TypeRecordKind.Class)
                    return ReturnKind.SwiftClass;
                if (typeRecord.Kind == TypeRecordKind.Struct)
                {
                    bool isFrozen = typeRecord.Flags.HasFlag(TypeRecordFlags.Frozen);
                    bool hasRefFields = typeRecord.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement);
                    if (isFrozen && !hasRefFields)
                        return ReturnKind.FrozenStruct;
                    return ReturnKind.NonFrozenStruct;
                }
                if (typeRecord.Kind == TypeRecordKind.Enum && typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                    return ReturnKind.Primitive;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Classifies a parameter TypeSpec for marshalling.
    /// Returns null if the type is not supported.
    /// </summary>
    public static ParamKind? ClassifyParameterType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return null;

        if (namedType.ContainsGenericParameters)
            return null;

        if (MarshallingHelpers.IsSwiftPrimitive(namedType.Name))
            return ParamKind.Primitive;

        if (MarshallingHelpers.TypeAliasToCSPrimitive.ContainsKey(namedType.Name))
            return ParamKind.Primitive;

        try
        {
            if (TypeDatabaseExtensions.IsObjCModuleType(namedType))
                return ParamKind.ObjCClass;

            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            {
                // ObjC-rooted classes (Swift classes inheriting NSObject) use .Handle like ObjC classes
                if (typeRecord.Kind == TypeRecordKind.Class && MarshallingHelpers.IsObjCRooted(typeRecord))
                    return ParamKind.ObjCClass;
                if (typeRecord.Kind == TypeRecordKind.Class)
                    return ParamKind.SwiftClass;
                if (typeRecord.Kind == TypeRecordKind.Struct)
                {
                    bool isFrozen = typeRecord.Flags.HasFlag(TypeRecordFlags.Frozen);
                    bool hasRefFields = typeRecord.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement);
                    if (isFrozen && !hasRefFields)
                        return ParamKind.FrozenStruct;
                    return null;
                }
                if (typeRecord.Kind == TypeRecordKind.Enum && typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                    return ParamKind.SimpleEnum;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Resolves a TypeSpec to its C# type name for use in public method signatures.
    /// </summary>
    public static string ResolveCSharpTypeName(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return "void";

        if (MarshallingHelpers.TypeAliasToCSPrimitive.TryGetValue(namedType.Name, out var aliased))
            return aliased;

        if (MarshallingHelpers.IsSwiftPrimitive(namedType.Name))
        {
            return namedType.Name switch
            {
                "Swift.Int" => "nint",
                "Swift.UInt" => "nuint",
                "Swift.Int8" => "sbyte",
                "Swift.Int16" => "short",
                "Swift.Int32" => "int",
                "Swift.Int64" => "long",
                "Swift.UInt8" => "byte",
                "Swift.UInt16" => "ushort",
                "Swift.UInt32" => "uint",
                "Swift.UInt64" => "ulong",
                "Swift.Float" => "float",
                "Swift.Double" => "double",
                "Swift.Bool" => "bool",
                "CoreFoundation.CGFloat" => "nfloat",
                "CoreFoundation.CGSize" => "CoreGraphics.CGSize",
                "CoreFoundation.CGPoint" => "CoreGraphics.CGPoint",
                "CoreFoundation.CGRect" => "CoreGraphics.CGRect",
                _ => namedType.Name,
            };
        }

        if (typeDatabase.TryGetTypeRecord(namedType, out var typeRecord))
            return typeRecord.CSharpTypeName.FullyQualifiedName;

        return namedType.Name;
    }

    /// <summary>
    /// Resolves the P/Invoke return type for a given return kind.
    /// </summary>
    public static string ResolvePInvokeReturnType(TypeSpec? typeSpec, ReturnKind category, ITypeDatabase typeDatabase, bool usesIndirectResult)
    {
        if (usesIndirectResult)
            return "void";

        return category switch
        {
            ReturnKind.Void => "void",
            ReturnKind.Primitive => typeSpec is NamedTypeSpec n && n.Name == "Swift.Bool"
                ? "bool"
                : ResolveCSharpTypeName(typeSpec!, typeDatabase),
            ReturnKind.ObjCClass => "IntPtr",
            ReturnKind.SwiftClass => "IntPtr",
            ReturnKind.NonFrozenStruct => "void", // shouldn't reach here
            _ => "void",
        };
    }

    /// <summary>
    /// Resolves the P/Invoke parameter type based on param kind.
    /// </summary>
    public static string ResolvePInvokeParamType(TypeSpec typeSpec, ParamKind category, ITypeDatabase typeDatabase)
    {
        return category switch
        {
            ParamKind.Primitive => ResolveCSharpTypeName(typeSpec, typeDatabase),
            ParamKind.ObjCClass => "IntPtr",
            ParamKind.SwiftClass => "IntPtr",
            ParamKind.SimpleEnum => ResolveCSharpTypeName(typeSpec, typeDatabase),
            _ => "IntPtr",
        };
    }

    /// <summary>
    /// Gets the P/Invoke argument expression for a parameter based on its kind.
    /// </summary>
    public static string GetPInvokeArgExpression(string paramName, ParamKind category)
    {
        return category switch
        {
            ParamKind.Primitive => paramName,
            ParamKind.ObjCClass => $"{paramName}.Handle",
            ParamKind.SwiftClass => $"{paramName}.Payload.DangerousGetHandle()",
            ParamKind.SimpleEnum => paramName,
            _ => paramName,
        };
    }

    /// <summary>
    /// Emits return value marshalling based on the return kind.
    /// Shared between ForeignType and CrossModule extension emitters.
    /// </summary>
    public static void EmitReturnValueMarshalling(
        CSharpWriter csWriter,
        ReturnKind returnCategory,
        string nativeCall,
        string csharpType)
    {
        switch (returnCategory)
        {
            case ReturnKind.Void:
                csWriter.WriteLine($"{nativeCall};");
                break;

            case ReturnKind.Primitive:
                csWriter.WriteLine($"return {nativeCall};");
                break;

            case ReturnKind.ObjCClass:
                csWriter.WriteLine($"var result = {nativeCall};");
                csWriter.WriteLine($"return {MarshallingHelpers.FormatObjCBridgeCall(csharpType, "result", nonNull: true)};");
                break;

            case ReturnKind.SwiftClass:
                csWriter.WriteLine($"var result = {nativeCall};");
                csWriter.WriteLine($"return ({csharpType})SwiftMarshal.MarshalFromSwift<{csharpType}>(result);");
                break;

            case ReturnKind.NonFrozenStruct:
                csWriter.WriteLines($$"""
                    unsafe
                    {
                        var metadata = SwiftObjectHelper<{{csharpType}}>.GetTypeMetadata();
                        IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                        try
                        {
                            var indirectResult = new SwiftIndirectResult((void*)buffer);
                            {{nativeCall}};
                            return SwiftMarshal.MarshalFromSwift<{{csharpType}}>(buffer);
                        }
                        catch
                        {
                            NativeMemory.Free((void*)buffer);
                            throw;
                        }
                    }
                    """);
                break;
        }
    }
}
