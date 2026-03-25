// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Identifies which ARM64 register file a slot belongs to.
/// </summary>
public enum RegisterFile
{
    /// <summary>General-purpose integer registers (x0-x3).</summary>
    Integer,
    /// <summary>Floating-point/SIMD registers (d0-d3).</summary>
    Float,
}

/// <summary>
/// Represents a single ARM64 register slot used for passing/returning a value.
/// </summary>
/// <param name="File">Which register file (integer or float) this slot uses.</param>
/// <param name="Index">The index within its register file (e.g., 0 for x0/d0).</param>
/// <param name="ByteSize">The byte size of the value in this slot (e.g., 8 for Int, 1 for Bool).</param>
public record RegisterSlot(RegisterFile File, int Index, int ByteSize);

/// <summary>
/// The result of lowering a Swift type to ARM64 register assignments.
/// </summary>
/// <param name="Slots">The register slots used. Empty for void or empty structs.</param>
/// <param name="IsIndirect">True if the value is passed/returned via an x8 pointer (too large for registers).</param>
/// <param name="TotalByteSize">Total byte size of the type.</param>
public record TypeLoweringResult(
    IReadOnlyList<RegisterSlot> Slots,
    bool IsIndirect,
    int TotalByteSize);

/// <summary>
/// Maps Swift types to physical ARM64 register slots following the Swift calling convention (swiftcc).
/// This is the foundation for generating native ARM64 thunks that bridge cdecl ↔ swiftcc.
///
/// Key rules (verified empirically — see RESEARCH.md in the experiments worktree):
/// - Scalars: Int/UInt/pointer → 1 integer slot. Float/Double → 1 float slot. Bool → 1 integer slot (1 byte).
/// - Structs: Recursively flatten fields. Count total slots (int + float combined).
/// - 4-slot limit: If total slots > 4, the value is passed indirectly via x8 pointer.
/// - Classes: Always 1 integer slot (pointer).
/// - Optional&lt;value type&gt;: Inner type's slots + 1 integer tag slot.
/// - Optional&lt;class&gt;: 1 integer slot (nullable pointer, no tag).
/// - Empty struct: 0 slots, not indirect.
/// - Non-frozen struct: Always indirect (unknown layout at compile time).
/// - Enums with raw value: 1 integer slot.
/// </summary>
public static class TypeLowering
{
    /// <summary>
    /// Maximum number of register slots for direct return/parameter passing in swiftcc.
    /// If a type requires more than this many slots, it is passed indirectly via pointer.
    /// </summary>
    public const int MaxDirectSlots = 4;

    /// <summary>
    /// Lowers a Swift return type to ARM64 register assignments.
    /// </summary>
    /// <param name="typeSpec">The Swift type specification to lower.</param>
    /// <param name="typeDb">Type database for resolving struct/class/enum metadata.</param>
    /// <returns>The lowering result, or null if the type cannot be lowered (layout unknown).</returns>
    public static TypeLoweringResult? LowerReturnType(TypeSpec typeSpec, ITypeDatabase typeDb)
    {
        return LowerType(typeSpec, typeDb);
    }

    /// <summary>
    /// Lowers a Swift parameter type to ARM64 register assignments.
    /// Parameters follow the same rules as returns for register allocation.
    /// </summary>
    /// <param name="typeSpec">The Swift type specification to lower.</param>
    /// <param name="typeDb">Type database for resolving struct/class/enum metadata.</param>
    /// <returns>The lowering result, or null if the type cannot be lowered (layout unknown).</returns>
    public static TypeLoweringResult? LowerParameterType(TypeSpec typeSpec, ITypeDatabase typeDb)
    {
        return LowerType(typeSpec, typeDb);
    }

    /// <summary>
    /// Core lowering logic shared by return and parameter lowering.
    /// </summary>
    private static TypeLoweringResult? LowerType(TypeSpec typeSpec, ITypeDatabase typeDb)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return null; // Tuple, closure, protocol composition — can't lower

        return LowerNamedType(namedType, typeDb);
    }

    /// <summary>
    /// Lowers a named type (scalar, struct, class, enum, optional) to register slots.
    /// </summary>
    private static TypeLoweringResult? LowerNamedType(NamedTypeSpec namedType, ITypeDatabase typeDb)
    {
        // Try scalar primitives first (no database lookup needed)
        var scalarResult = TryLowerScalar(namedType.Name);
        if (scalarResult != null)
            return scalarResult;

        // Handle Optional<T>
        if (namedType.Name == "Swift.Optional" && namedType.GenericParameters.Count == 1)
            return LowerOptional(namedType, typeDb);

        // Handle typed pointers (UnsafePointer<T>, UnsafeMutablePointer<T>)
        if (namedType.Name is "Swift.UnsafePointer" or "Swift.UnsafeMutablePointer")
            return new TypeLoweringResult(
                new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                IsIndirect: false,
                TotalByteSize: 8);

        // Generic types without special handling can't be lowered
        if (namedType.ContainsGenericParameters)
            return null;

        // Existential types can't be lowered
        if (namedType.IsAny)
            return null;

        // Unqualified type names (e.g., "Self") can't be lowered — they have no module prefix
        if (!namedType.Name.Contains('.'))
            return null;

        // Look up in type database
        var swiftTypeName = SwiftTypeName.FromTypeSpec(namedType);
        if (!typeDb.TryGetTypeRecord(swiftTypeName, out var record))
            return null;

        return LowerFromTypeRecord(record, typeDb);
    }

    /// <summary>
    /// Lowers a type from its TypeRecord (used for database-resolved types).
    /// </summary>
    internal static TypeLoweringResult? LowerFromTypeRecord(TypeRecord record, ITypeDatabase typeDb)
    {
        switch (record.Kind)
        {
            case TypeRecordKind.Class:
                return new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8);

            case TypeRecordKind.Enum:
                // Non-frozen enums are passed indirectly in Swift ABI (resilient layout):
                // the compiler can't assume the size across module boundaries, so the caller
                // passes a pointer to the value. Thunks pass values directly in registers,
                // so only @frozen simple enums can be safely lowered.
                if (record.Flags.HasFlag(TypeRecordFlags.SimpleEnum)
                    && record.Flags.HasFlag(TypeRecordFlags.Frozen))
                    return new TypeLoweringResult(
                        new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                        IsIndirect: false,
                        TotalByteSize: record.InlineSize ?? 8);
                return null; // Non-frozen or complex enum — can't lower

            case TypeRecordKind.Struct:
                return LowerStruct(record);

            default:
                return null;
        }
    }

    /// <summary>
    /// Lowers a struct type using its ABI field layout.
    /// </summary>
    private static TypeLoweringResult? LowerStruct(TypeRecord record)
    {
        // Non-frozen structs are always indirect (unknown layout)
        if (!record.Flags.HasFlag(TypeRecordFlags.Frozen))
            return new TypeLoweringResult(
                Array.Empty<RegisterSlot>(),
                IsIndirect: true,
                TotalByteSize: 0);

        // Empty frozen struct (no stored properties, e.g., Void-like)
        if (string.IsNullOrEmpty(record.AbiFieldLayout))
        {
            // If we have InlineSize = 0, it's truly empty
            if (record.InlineSize.HasValue && record.InlineSize.Value == 0)
                return new TypeLoweringResult(
                    Array.Empty<RegisterSlot>(),
                    IsIndirect: false,
                    TotalByteSize: 0);

            // Frozen struct without layout info — can't lower for thunks
            // This is the safe fallback: route to @_cdecl instead
            return null;
        }

        // Parse the ABI field layout string (e.g., "i,f,i,f")
        var fields = record.AbiFieldLayout.Split(',');
        var slots = new List<RegisterSlot>();
        int intIndex = 0;
        int floatIndex = 0;
        int totalBytes = 0;

        foreach (var field in fields)
        {
            switch (field.Trim())
            {
                case "i":
                    slots.Add(new RegisterSlot(RegisterFile.Integer, intIndex++, 8));
                    totalBytes += 8;
                    break;
                case "f":
                    slots.Add(new RegisterSlot(RegisterFile.Float, floatIndex++, 8));
                    totalBytes += 8;
                    break;
                case "b":
                    slots.Add(new RegisterSlot(RegisterFile.Integer, intIndex++, 1));
                    totalBytes += 1; // Bool is 1 byte, padded to register slot
                    break;
                case "p":
                    slots.Add(new RegisterSlot(RegisterFile.Integer, intIndex++, 8));
                    totalBytes += 8;
                    break;
                default:
                    return null; // Unknown field type
            }
        }

        // 4-slot limit: if total slots exceed MaxDirectSlots, pass indirectly
        if (slots.Count > MaxDirectSlots)
            return new TypeLoweringResult(
                slots.AsReadOnly(),
                IsIndirect: true,
                TotalByteSize: record.InlineSize ?? totalBytes);

        return new TypeLoweringResult(
            slots.AsReadOnly(),
            IsIndirect: false,
            TotalByteSize: record.InlineSize ?? totalBytes);
    }

    /// <summary>
    /// Lowers Optional&lt;T&gt; — nullable class pointers are 1 integer slot,
    /// value type optionals are inner layout + 1 integer tag slot.
    /// </summary>
    private static TypeLoweringResult? LowerOptional(NamedTypeSpec optionalType, ITypeDatabase typeDb)
    {
        if (optionalType.GenericParameters[0] is not NamedTypeSpec innerType)
            return null;

        // Unqualified type names (e.g., "Self") can't be resolved
        if (!innerType.Name.Contains('.'))
            return null;

        // Check if inner type is a class (Optional<class> = nullable pointer, no tag)
        var swiftTypeName = SwiftTypeName.FromTypeSpec(innerType);
        if (typeDb.TryGetTypeRecord(swiftTypeName, out var innerRecord) && innerRecord.Kind == TypeRecordKind.Class)
            return new TypeLoweringResult(
                new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                IsIndirect: false,
                TotalByteSize: 8);

        // Value type optional: lower inner type, then add tag slot
        var innerResult = LowerNamedType(innerType, typeDb);
        if (innerResult == null || innerResult.IsIndirect)
            return null; // Can't compose optional of indirect type in registers

        var slots = new List<RegisterSlot>(innerResult.Slots);
        // Add the tag byte as an integer slot
        int nextIntIndex = slots.Count(s => s.File == RegisterFile.Integer);
        slots.Add(new RegisterSlot(RegisterFile.Integer, nextIntIndex, 1));

        int totalSize = innerResult.TotalByteSize + 1; // +1 byte for tag

        if (slots.Count > MaxDirectSlots)
            return new TypeLoweringResult(
                slots.AsReadOnly(),
                IsIndirect: true,
                TotalByteSize: totalSize);

        return new TypeLoweringResult(
            slots.AsReadOnly(),
            IsIndirect: false,
            TotalByteSize: totalSize);
    }

    /// <summary>
    /// Tries to lower a scalar type by its fully-qualified Swift name.
    /// Returns null if the type is not a recognized scalar.
    /// </summary>
    private static TypeLoweringResult? TryLowerScalar(string swiftTypeName)
    {
        return swiftTypeName switch
        {
            // Integer types — all map to 1 integer register slot
            "Swift.Int" or "Swift.UInt" => new TypeLoweringResult(
                new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                IsIndirect: false, TotalByteSize: 8),

            "Swift.Int8" or "Swift.UInt8" => new TypeLoweringResult(
                new[] { new RegisterSlot(RegisterFile.Integer, 0, 1) },
                IsIndirect: false, TotalByteSize: 1),

            "Swift.Int16" or "Swift.UInt16" => new TypeLoweringResult(
                new[] { new RegisterSlot(RegisterFile.Integer, 0, 2) },
                IsIndirect: false, TotalByteSize: 2),

            "Swift.Int32" or "Swift.UInt32" => new TypeLoweringResult(
                new[] { new RegisterSlot(RegisterFile.Integer, 0, 4) },
                IsIndirect: false, TotalByteSize: 4),

            "Swift.Int64" or "Swift.UInt64" => new TypeLoweringResult(
                new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                IsIndirect: false, TotalByteSize: 8),

            // Bool — 1 byte, integer register
            "Swift.Bool" => new TypeLoweringResult(
                new[] { new RegisterSlot(RegisterFile.Integer, 0, 1) },
                IsIndirect: false, TotalByteSize: 1),

            // Float types — map to float register slots
            "Swift.Float" => new TypeLoweringResult(
                new[] { new RegisterSlot(RegisterFile.Float, 0, 4) },
                IsIndirect: false, TotalByteSize: 4),

            "Swift.Double" or "CoreFoundation.CGFloat" or "CoreGraphics.CGFloat" => new TypeLoweringResult(
                new[] { new RegisterSlot(RegisterFile.Float, 0, 8) },
                IsIndirect: false, TotalByteSize: 8),

            // Pointer types — 1 integer register slot
            "Swift.OpaquePointer" or "Swift.UnsafeRawPointer" or "Swift.UnsafeMutableRawPointer" => new TypeLoweringResult(
                new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                IsIndirect: false, TotalByteSize: 8),

            _ => null,
        };
    }
}
