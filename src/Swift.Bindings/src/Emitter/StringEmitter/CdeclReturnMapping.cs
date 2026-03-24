// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Describes the @_cdecl return type mapping for a Swift return type.
/// Extracted from PropertyWrapperEmitter to a shared location — referenced from 10+ files.
/// </summary>
internal record CdeclReturnMapping(string CdeclReturnType, CdeclReturnKind Kind)
{
    /// <summary>
    /// Classifies a Swift return type into its @_cdecl return mapping.
    /// All dependencies are on <see cref="CdeclParamMapper"/> (shared) — no emitter-specific calls.
    /// </summary>
    internal static (CdeclReturnMapping mapping, bool needsResultPtr) Classify(
        TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        // DynamicSelf (Self): resolves to parent class type at call site.
        // Return as class pointer (Unmanaged.passRetained().toOpaque()).
        if (typeSpec.IsDynamicSelf)
            return (new CdeclReturnMapping("UnsafeMutableRawPointer", CdeclReturnKind.ClassPointer), false);

        // Tuple returns: route through indirect result (resultPtr buffer).
        // initializeMemory(as: (T1, T2).self) handles all tuple element types.
        if (typeSpec is TupleTypeSpec tts && !tts.IsEmptyTuple)
            return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);

        // Primitives: pass through directly
        if (CdeclParamMapper.IsCdeclPrimitive(typeSpec))
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
            if (MarshallingHelpers.IsBoolType(swiftType) || swiftType == "Bool")
                return (new CdeclReturnMapping("Int8", CdeclReturnKind.Bool), false);
            return (new CdeclReturnMapping(swiftType, CdeclReturnKind.Direct), false);
        }

        // String: SBW_Utf8Slice via result pointer (@_cdecl can't return Swift structs)
        if (typeSpec is NamedTypeSpec strNamed && strNamed.Name == "Swift.String")
            return (new CdeclReturnMapping("SBW_Utf8Slice", CdeclReturnKind.String), true);

        // AnyObject: IS a class reference by definition — use Unmanaged.passRetained().toOpaque().
        // AnyObject may appear as ProtocolListTypeSpec (from existential parsing) or as plain
        // NamedTypeSpec (from TypeSpecParser). Without this gate, it falls through to IndirectResult
        // and emits `any AnyObject.self` which is not valid Swift.
        if (CdeclParamMapper.IsAnyObjectType(typeSpec))
            return (new CdeclReturnMapping("UnsafeMutableRawPointer", CdeclReturnKind.ClassPointer), false);

        // Closure returns: write to resultPtr buffer
        if (typeSpec is ClosureTypeSpec)
            return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);

        // Optional<reference type>: nullable pointer ABI (no result buffer needed)
        if (CdeclParamMapper.IsOptionalWithReferenceInner(typeSpec, typeDatabase))
            return (new CdeclReturnMapping("UnsafeMutableRawPointer?", CdeclReturnKind.OptionalClassPointer), false);

        // Generic containers (Optional, Array, etc.): need result pointer
        if (CdeclParamMapper.IsGenericContainerType(typeSpec))
            return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);

        // Protocol existentials: need result pointer (not C-representable in @_cdecl)
        if (CdeclParamMapper.IsProtocolExistentialType(typeSpec, typeDatabase))
            return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);

        // Try TypeRecord-based mapping
        if (typeDatabase.TryGetTypeRecord(typeSpec, out var typeRecord))
        {
            // NSString typedef structs (e.g., CALayerContentsGravity, CATransitionType) are ObjC-bridged
            // in the type database but are Swift structs wrapping NSString — not class instances.
            // Unmanaged.passRetained() requires a class, so these must NOT use ClassPointer.
            // Route through indirect result like other structs.
            if (MarshallingHelpers.IsObjCBridged(typeRecord) &&
                typeSpec is NamedTypeSpec nsTypedef &&
                AppleFrameworkRegistry.TryGetNetTypeName(nsTypedef.Name, out var remapped) &&
                remapped == "Foundation.NSString")
                return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);

            // Classes and ObjC-bridged: return as retained pointer.
            // Guard: Unmanaged.passRetained() requires a class type — ObjC-rooted/bridged struct
            // types (e.g., PHPickerResult) must fall through to IndirectResult instead.
            if (typeRecord.Kind == TypeRecordKind.Class ||
                ((MarshallingHelpers.IsObjCBridged(typeRecord) || MarshallingHelpers.IsObjCRooted(typeRecord))
                 && typeRecord.Kind != TypeRecordKind.Struct))
                return (new CdeclReturnMapping("UnsafeMutableRawPointer", CdeclReturnKind.ClassPointer), false);

            // ObjC-bridgeable value types (URL): bridge to ObjC class pointer via `as AnyObject`.
            if (MarshallingHelpers.IsObjCBridgeable(typeRecord))
                return (new CdeclReturnMapping("UnsafeMutableRawPointer", CdeclReturnKind.ClassPointer), false);

            // Simple enums: return raw value type
            if (typeRecord.Kind == TypeRecordKind.Enum && typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            {
                var rawType = CdeclParamMapper.GetSwiftRawValueType(typeRecord.RawValueTypeName);
                return (new CdeclReturnMapping(rawType, CdeclReturnKind.SimpleEnum), false);
            }

            // Complex enums: need result pointer
            if (typeRecord.Kind == TypeRecordKind.Enum)
                return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);

            // All structs (frozen and non-frozen): need result pointer.
            // @_cdecl can't return Swift structs — even @frozen ones fail with
            // "result type cannot be represented in Objective-C".
            return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);
        }

        // Fallback: indirect result
        return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);
    }
}

/// <summary>
/// Categories of @_cdecl return type handling.
/// </summary>
internal enum CdeclReturnKind
{
    Direct,               // Primitive, frozen struct — return by value
    Bool,                 // Bool → Int8 conversion
    String,               // String → SBW_Utf8Slice
    SimpleEnum,           // Enum → raw value type
    ClassPointer,         // Class → Unmanaged.passRetained().toOpaque()
    OptionalClassPointer, // Optional<Class> → result.map { Unmanaged.passRetained($0).toOpaque() }
    IndirectResult        // Non-frozen struct, complex enum → writes to resultPtr
}
