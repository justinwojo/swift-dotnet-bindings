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
    /// Preferred spelling of the local holding the indirect-result register for a resilient
    /// struct return. The call expression is built by one emitter and the declaration by
    /// <see cref="EmitReturnValueMarshalling"/>, so both mint from this one constant against the
    /// member's scope rather than each writing the literal.
    /// </summary>
    public const string IndirectResultLocalName = "indirectResult";

    /// <summary>
    /// Preferred spelling of the receiver parameter every emitted extension member declares. It
    /// shares the body scope with the generated locals exactly as the Swift-derived parameters do,
    /// so it is minted against them rather than written as a literal.
    /// </summary>
    private const string ReceiverParameterName = "self";

    /// <summary>
    /// Name of the synthesized parameter an emitted extension property setter takes the new value
    /// under. A setter has no Swift-authored parameter list, so this is the whole of it, and the
    /// receiver mints against it exactly as a method's receiver mints against its projected names.
    /// </summary>
    public const string SetterValueParameterName = "value";

    /// <summary>
    /// The name an extension member's <c>this</c> receiver is declared under, paired with the body
    /// scope its generated locals mint from.
    /// </summary>
    /// <remarks>
    /// <c>self</c> is a legal Swift argument label, so a member can project a parameter spelled
    /// exactly like the receiver. The receiver is therefore minted FIRST, against the projected
    /// parameter names — never the reverse: the receiver is the <c>this</c> parameter, so moving it
    /// is invisible to every caller, while moving a user's parameter would break named-argument
    /// call sites. A mint with nothing colliding returns <c>self</c>, so the overwhelmingly common
    /// case emits exactly what it did before.
    /// </remarks>
    public readonly struct ExtensionReceiverScope
    {
        internal ExtensionReceiverScope(string receiverName, SyntheticNameScope bodyScope)
        {
            ReceiverName = receiverName;
            BodyScope = bodyScope;
        }

        /// <summary>The identifier the <c>this</c> receiver is declared and referenced under.</summary>
        public string ReceiverName { get; }

        /// <summary>
        /// The member's body scope. Already holds the projected parameter names and the receiver,
        /// so any local minted from it moves aside from all of them.
        /// </summary>
        public SyntheticNameScope BodyScope { get; }
    }

    /// <summary>
    /// Builds the receiver name and the body scope for one emitted extension member, seeded with
    /// the identifiers already live in it: the member's projected parameter names, then the
    /// receiver minted against them. Generated locals minted from the returned scope move aside
    /// from both; the public parameters never do.
    /// </summary>
    public static ExtensionReceiverScope BuildReceiverScope(IEnumerable<string>? parameterNames)
    {
        var scope = new SyntheticNameScope(parameterNames ?? Enumerable.Empty<string>());
        return new ExtensionReceiverScope(scope.Mint(ReceiverParameterName), scope);
    }

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
    /// True when a return classified <see cref="ReturnKind.NonFrozenStruct"/> really is a resilient
    /// struct — the shape whose value the caller materializes out of a buffer it supplied, as the
    /// opaque-payload C# carrier.
    /// </summary>
    /// <remarks>
    /// <see cref="ReturnKind.NonFrozenStruct"/> is a two-member bucket. One member is a genuinely
    /// non-<c>@frozen</c> struct: the caller cannot know its layout, so the value only ever reaches
    /// it through a buffer, and the C# projection of that type is a handle-backed carrier that can
    /// adopt one. The other is a <c>@frozen</c> struct carrying reference fields
    /// (<c>Swift.String</c>, <c>Swift.Array</c>) — its layout IS known, so a direct call hands it
    /// back in registers and leaves a caller-allocated buffer untouched, and its C# projection is
    /// not a buffer-adopting carrier either. An ObjC-bridged or ObjC-bridgeable struct is excluded
    /// for the same reason: it crosses the boundary as a class pointer. Only the first member can
    /// be read back out of a result buffer, so an arm that allocates one must ask this rather than
    /// keying on the <see cref="ReturnKind"/> alone.
    /// </remarks>
    public static bool MaterializesFromResultBuffer(TypeSpec? returnTypeSpec, ITypeDatabase typeDatabase)
    {
        if (returnTypeSpec is not NamedTypeSpec namedType || namedType.ContainsGenericParameters)
            return false;

        try
        {
            if (TypeDatabaseExtensions.IsObjCModuleType(namedType))
                return false;

            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
                return false;
            if (typeRecord.Kind != TypeRecordKind.Struct)
                return false;
            if (MarshallingHelpers.IsObjCBridged(typeRecord) || MarshallingHelpers.IsObjCBridgeable(typeRecord))
                return false;

            return !MarshallingHelpers.IsTypeFrozen(typeRecord);
        }
        catch (ArgumentException)
        {
            return false;
        }
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
    /// <param name="bodyScope">
    /// Names the locals this method declares. The extension emitters put the member's projected
    /// parameter names directly into the same C# method scope with no intervening block, and a
    /// Swift signature is free to spell any of them — so the locals are minted rather than
    /// hardcoded. The scope is the caller's, seeded with those parameter names, and
    /// <see cref="SyntheticNameScope.Mint"/> is idempotent per spelling, so a caller that already
    /// minted the same local while building <paramref name="nativeCall"/> gets the same name here.
    /// </param>
    public static void EmitReturnValueMarshalling(
        CSharpWriter csWriter,
        ReturnKind returnCategory,
        string nativeCall,
        string csharpType,
        SyntheticNameScope bodyScope,
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
            {
                var resultLocal = bodyScope.Mint("result");
                csWriter.WriteLine($"var {resultLocal} = {nativeCall};");
                csWriter.WriteLine($"return {MarshallingHelpers.FormatObjCBridgeCall(csharpType, resultLocal, nonNull: true)};");
                break;
            }

            case ReturnKind.SwiftClass:
            {
                var resultLocal = bodyScope.Mint("result");
                csWriter.WriteLine($"var {resultLocal} = {nativeCall};");
                csWriter.WriteLine($"return ({csharpType})SwiftMarshal.MarshalFromSwift<{csharpType}>({resultLocal});");
                break;
            }

            case ReturnKind.NonFrozenStruct:
                EmitResultBufferedReturn(csWriter, csharpType, bodyScope, bufferLocal =>
                {
                    // The direct swiftcc route carries the buffer in the indirect-result register,
                    // so the register wrapper is declared here and the caller's nativeCall names it.
                    var indirectResultLocal = bodyScope.Mint(IndirectResultLocalName);
                    csWriter.WriteLine($"var {indirectResultLocal} = new SwiftIndirectResult((void*){bufferLocal});");
                    csWriter.WriteLine($"{nativeCall};");
                });
                break;
        }
    }

    /// <summary>
    /// Emits the whole caller-supplied-buffer protocol for a resilient struct return: size the
    /// buffer from the type's own metadata, hand it to <paramref name="emitCall"/> to make the
    /// native call against, then materialize the C# value out of it.
    /// </summary>
    /// <remarks>
    /// Shared so the two routes that reach a resilient struct — the direct swiftcc call, which
    /// carries the buffer in the indirect-result register, and a generated <c>@_cdecl</c>
    /// trampoline, which takes it as an ordinary pointer parameter — cannot drift on the part that
    /// is identical: who allocates, how big, and who owns it afterwards. Ownership is the reason
    /// the free lives only on the throwing path: the carrier adopts the buffer on success and frees
    /// it when the handle is released, so freeing here too would be a double free.
    /// </remarks>
    /// <param name="emitCall">
    /// Writes the native call, given the name of the local holding the buffer pointer. Anything it
    /// needs to declare alongside the call (a register wrapper, a transfer flag) belongs here — the
    /// lines land inside the <c>try</c>, after the allocation and before the value is read back.
    /// </param>
    public static void EmitResultBufferedReturn(
        CSharpWriter csWriter,
        string csharpType,
        SyntheticNameScope bodyScope,
        Action<string> emitCall)
    {
        var metadataLocal = bodyScope.Mint("metadata");
        var bufferLocal = bodyScope.Mint("buffer");

        csWriter.WriteLine("unsafe");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"var {metadataLocal} = SwiftObjectHelper<{csharpType}>.GetTypeMetadata();");
        csWriter.WriteLine($"IntPtr {bufferLocal} = (IntPtr)NativeMemory.Alloc({metadataLocal}.Size);");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        emitCall(bufferLocal);
        csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{csharpType}>({bufferLocal});");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("catch");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"NativeMemory.Free((void*){bufferLocal});");
        csWriter.WriteLine("throw;");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");
    }
}
