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
    /// Resolves a SimpleEnum TypeSpec to the raw-integer lowering used at the cdecl/silgen
    /// boundary: the C# underlying integer name (e.g. "int"), the matching Swift scalar (e.g.
    /// "Int32"), and the fully module-qualified Swift enum name for <c>T(rawValue:)</c>
    /// reconstruction. Shared by both extension emitters so a SimpleEnum parameter is never
    /// treated as an object pointer (<c>Unmanaged&lt;AnyObject&gt;</c> is illegal for a
    /// non-class type) — enums cross the raw-text/silgen boundary as their raw scalar, not a
    /// pointer, same as the ABI-JSON cdecl boundary.
    ///
    /// Returns false for any enum that cannot be lowered to a single integer: String-raw enums
    /// (not blittable across the C ABI) and no-raw simple enums (no <c>init(rawValue:)</c> /
    /// <c>.rawValue</c>). Callers must treat a false return as "unsupported" and skip the
    /// member/parameter rather than fall through to a pointer-based marshal.
    /// </summary>
    public static bool TryGetSimpleEnumLowering(
        TypeSpec typeSpec,
        ITypeDatabase typeDatabase,
        out string? underlyingCSType,
        out string? underlyingSwiftType,
        out string? qualifiedSwiftType)
    {
        underlyingCSType = null;
        underlyingSwiftType = null;
        qualifiedSwiftType = null;

        if (typeSpec is not NamedTypeSpec named)
            return false;

        SwiftTypeName swiftTypeName;
        try
        {
            swiftTypeName = SwiftTypeName.FromModuleQualifiedName(named.Name);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return false;
        if (record.Kind != TypeRecordKind.Enum || !record.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            return false;

        // String-raw enums are not blittable across the C ABI.
        // No-raw simple enums lack init(rawValue:) / .rawValue — routing them through
        // the integer-raw lowering would emit Swift that fails to compile.
        if (string.IsNullOrEmpty(record.RawValueTypeName) || record.RawValueTypeName == "String")
            return false;

        underlyingCSType = EnumHandler.GetCSharpEnumUnderlyingType(record.RawValueTypeName);
        underlyingSwiftType = EnumHandler.GetSwiftScalarType(underlyingCSType);
        qualifiedSwiftType = record.SwiftTypeName.ModuleQualifiedName;
        return true;
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
    /// True when <paramref name="typeSpec"/> resolves to an Apple-framework type that is absent
    /// from the .NET binding surface — one that flattens to a synthesized ObjC-bridged class record
    /// marked <see cref="TypeRecordFlags.AbsentAppleProjection"/> (or an absent frozen value type the
    /// classifier reports as <see cref="ValidationRuleSet.UnsupportedReferenceKind.AbsentBridgedValueType"/>),
    /// or contains such a type in a generic argument. Emitting a reference to one dangles as a
    /// CS0234/CS0721/CS1061 against a type Microsoft.iOS never declares.
    /// <para>
    /// This is the shared absent-Apple ingress gate for the extension emitters
    /// (<see cref="ForeignTypeExtensionEmitter"/> and <see cref="CrossModuleExtensionEmitter"/>),
    /// which resolve foreign param/return/property C# type names straight off the TypeRecord. The
    /// coarse cdecl-compatibility classifiers (<see cref="ClassifyParameterType"/> /
    /// <see cref="ClassifyReturnType"/>) are blind to this — they treat any auto-bridge module type as
    /// a marshalable ObjC-class pointer — so each ingress must gate here and withdraw the member.
    /// </para>
    /// <para>
    /// The direct flag check closes the nested-type gap the classifier's cheap SwiftTypeName precheck
    /// leaves: a nested Apple type whose OUTER name is a registered bridged type — e.g.
    /// <c>Foundation.Calendar.Component</c>, whose outer <c>Foundation.Calendar</c> is the bridged
    /// NSCalendar — short-circuits that precheck, so the classifier reports the reference as supported
    /// even though emission still resolves the full nested spec to the flattened, surface-absent
    /// <c>Foundation.CalendarComponent</c> record. Gating on the record emission resolves keeps the
    /// withdrawal decision identical to what would be printed.
    /// </para>
    /// </summary>
    public static bool ReferencesAbsentAppleType(TypeSpec? typeSpec, ITypeDatabase typeDatabase, out string? offendingType)
    {
        offendingType = null;
        if (typeSpec == null)
            return false;

        if (ValidationRuleSet.ClassifyUnsupportedReference(typeSpec, typeDatabase, out offendingType)
                == ValidationRuleSet.UnsupportedReferenceKind.AbsentBridgedValueType)
            return true;

        // The direct-flag arm must recurse through the SAME container shapes the classifier walks
        // (tuple / closure / protocol-composition / generic parameters). The classifier's own
        // AbsentAppleProjection catch is bypassed for a nested Apple type whose outer name resolves
        // (the cheap SwiftTypeName precheck short-circuits), so a nested-absent type carried inside a
        // tuple or closure element would slip past unless this arm re-walks those elements too.
        switch (typeSpec)
        {
            case NamedTypeSpec namedType:
                if (namedType.HasModule() &&
                    typeDatabase.TryGetTypeRecord(namedType, out var record) &&
                    record.Flags.HasFlag(TypeRecordFlags.AbsentAppleProjection))
                {
                    offendingType = namedType.ToString();
                    return true;
                }
                foreach (var genericParam in namedType.GenericParameters)
                {
                    if (ReferencesAbsentAppleType(genericParam, typeDatabase, out offendingType))
                        return true;
                }
                return false;

            case TupleTypeSpec tupleType:
                foreach (var element in tupleType.Elements)
                {
                    if (ReferencesAbsentAppleType(element, typeDatabase, out offendingType))
                        return true;
                }
                return false;

            case ClosureTypeSpec closureType:
                return ReferencesAbsentAppleType(closureType.Arguments, typeDatabase, out offendingType)
                    || ReferencesAbsentAppleType(closureType.ReturnType, typeDatabase, out offendingType);

            case ProtocolListTypeSpec protocolList:
                foreach (var protocol in protocolList.Protocols.Keys)
                {
                    if (ReferencesAbsentAppleType(protocol, typeDatabase, out offendingType))
                        return true;
                }
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Resolves the P/Invoke return type for a given return kind.
    /// </summary>
    public static string ResolvePInvokeReturnType(TypeSpec? typeSpec, ReturnKind category, ITypeDatabase typeDatabase, bool usesIndirectResult)
    {
        if (usesIndirectResult)
            return "void";

        // A SimpleEnum crosses the silgen boundary as its raw scalar (see
        // EmitSwiftMethodWrapper/EmitSwiftPropertyGetter in ForeignTypeExtensionEmitter) —
        // the P/Invoke declaration must match that raw type, never the enum type itself,
        // or CallConvSwift reads the wrong-sized/shaped value out of the return register.
        if (category == ReturnKind.Primitive && typeSpec != null &&
            TryGetSimpleEnumLowering(typeSpec, typeDatabase, out var simpleEnumUnderlyingCS, out _, out _))
        {
            return simpleEnumUnderlyingCS!;
        }

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
        string csharpType,
        bool primitiveReturnNeedsEnumCast = false)
    {
        switch (returnCategory)
        {
            case ReturnKind.Void:
                csWriter.WriteLine($"{nativeCall};");
                break;

            case ReturnKind.Primitive:
                // A SimpleEnum crosses the silgen boundary as its raw underlying scalar (see
                // ResolvePInvokeReturnType and EmitSwiftMethodWrapper's `.rawValue` reconstruction) —
                // the public C# method still returns the enum type, so cast the raw P/Invoke
                // result back here.
                csWriter.WriteLine(primitiveReturnNeedsEnumCast
                    ? $"return ({csharpType}){nativeCall};"
                    : $"return {nativeCall};");
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
