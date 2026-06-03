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
    /// Lowers a frozen struct to swiftcc register slots by classifying its fields into eightbytes
    /// (8-byte chunks), mirroring how swiftcc expands an aggregate into registers:
    /// <list type="bullet">
    /// <item>integer fields that share an eightbyte coalesce into ONE general-purpose register
    /// (so the slot count reflects the eightbyte count, not the field count — a
    /// <c>{Int8 × 5, Int64, Int64}</c> is three eightbytes / three GPRs, returned directly by
    /// swiftcc, NOT seven slots forced indirect);</item>
    /// <item>each floating-point field keeps its own FP register (swiftcc does not merge floats the
    /// way the System V C ABI packs two into one SSE eightbyte).</item>
    /// </list>
    /// Field offsets are reconstructed from natural C/Swift alignment and cross-checked against the
    /// record's <see cref="TypeRecord.InlineSize"/>. A struct whose natural-aligned size does not
    /// match (explicit packing, or nested aggregates with interior padding the flattened layout
    /// string cannot express) is declined (null) so the caller routes it to the @_cdecl wrapper,
    /// whose C-ABI call is correct by construction. Likewise an eightbyte that mixes integer and
    /// floating-point fields, or packs more than one float, diverges from at least one target C ABI
    /// and is declined.
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

        // Parse the ABI field layout string. Each fragment is a register-file letter (i/f/b/p)
        // followed by the field's byte width (e.g., "i4,f4,i8,f8"). A bare letter with no width
        // is the legacy encoding and is treated as a full 8-byte slot (1 byte for bool), preserving
        // behaviour for type databases produced before widths were tracked. While parsing we
        // reconstruct each field's natural-aligned byte offset so fields can be bucketed into
        // eightbytes below.
        var fragments = record.AbiFieldLayout.Split(',');
        var fields = new List<(char Class, int Width, int Offset)>(fragments.Length);
        int cursor = 0;
        int structAlign = 1;

        foreach (var fragment in fragments)
        {
            if (!TryParseFieldFragment(fragment.Trim(), out char fieldClass, out int width))
                return null; // Unknown field type

            // Field offsets can only be reconstructed for scalar leaves whose alignment equals their
            // (power-of-two) width. A composite or unexpected width can't be modelled here — decline
            // to the @_cdecl wrapper rather than guess an offset.
            if (width != 1 && width != 2 && width != 4 && width != 8)
                return null;

            int align = width; // scalar alignment == width for the {1,2,4,8} domain
            cursor = AlignUp(cursor, align);
            fields.Add((fieldClass, width, cursor));
            cursor += width;
            structAlign = Math.Max(structAlign, align);
        }

        int reconstructedSize = AlignUp(cursor, structAlign);

        // Cross-check the natural-aligned reconstruction against the authoritative inline size. A
        // mismatch means the struct is packed, or contains nested aggregates whose interior padding
        // the flattened layout string does not capture — we cannot place its fields in registers
        // safely, so decline to the @_cdecl wrapper.
        if (record.InlineSize.HasValue && record.InlineSize.Value != reconstructedSize)
            return null;

        int totalBytes = record.InlineSize ?? reconstructedSize;

        // Group fields into eightbytes. A naturally-aligned scalar (≤ 8 bytes) never straddles an
        // 8-byte boundary, so each field belongs to exactly the eightbyte at offset / 8.
        var slots = new List<RegisterSlot>();
        int intIndex = 0;
        int floatIndex = 0;

        foreach (var group in fields.GroupBy(f => f.Offset / 8).OrderBy(g => g.Key))
        {
            var members = group.ToList();
            bool anyFloat = members.Any(m => m.Class == 'f');
            bool anyInt = members.Any(m => m.Class != 'f');

            // An eightbyte that mixes integer and floating-point fields diverges between swiftcc
            // (separate GPR + FP registers) and the System V C ABI (one packed INTEGER register), so
            // the field-wise return bridge cannot reproduce both — decline.
            if (anyFloat && anyInt)
                return null;

            if (anyFloat)
            {
                // Pure floating-point eightbyte. swiftcc keeps each float in its own FP register;
                // two floats packed into one eightbyte ({Float, Float}) is coalesced into a single
                // SSE register by System V, so it diverges. Allow only a lone float.
                if (members.Count != 1)
                    return null;
                slots.Add(new RegisterSlot(RegisterFile.Float, floatIndex++, members[0].Width));
            }
            else
            {
                // Pure-integer eightbyte → one general-purpose register. The store width spans this
                // eightbyte, capped at the struct's end so a trailing partial eightbyte never writes
                // past the buffer. Round to a width the backends can store (1/2/4/8); decline an
                // unmodellable partial size.
                int eightbyteBase = group.Key * 8;
                int byteSize = Math.Min(8, totalBytes - eightbyteBase);
                if (byteSize != 1 && byteSize != 2 && byteSize != 4 && byteSize != 8)
                    return null;
                slots.Add(new RegisterSlot(RegisterFile.Integer, intIndex++, byteSize));
            }
        }

        // More than four register slots: swiftcc passes/returns the aggregate indirectly via pointer.
        if (slots.Count > MaxDirectSlots)
            return new TypeLoweringResult(
                slots.AsReadOnly(),
                IsIndirect: true,
                TotalByteSize: totalBytes);

        return new TypeLoweringResult(
            slots.AsReadOnly(),
            IsIndirect: false,
            TotalByteSize: totalBytes);
    }

    /// <summary>Rounds <paramref name="offset"/> up to a multiple of the power-of-two <paramref name="alignment"/>.</summary>
    private static int AlignUp(int offset, int alignment) => (offset + (alignment - 1)) & ~(alignment - 1);

    /// <summary>
    /// Parses an ABI field-layout fragment into its register-file letter and byte width.
    /// Accepts the width-suffixed form ("i4", "f8", "b1", "p8") and the legacy bare-letter form
    /// ("i", "f", "b", "p"), which maps to the historical default slot width.
    /// </summary>
    private static bool TryParseFieldFragment(string fragment, out char fieldClass, out int width)
    {
        fieldClass = '\0';
        width = 0;
        if (string.IsNullOrEmpty(fragment))
            return false;

        fieldClass = fragment[0];
        int legacyWidth = fieldClass switch
        {
            'i' or 'p' or 'f' => 8,
            'b' => 1,
            _ => -1,
        };
        if (legacyWidth < 0)
            return false;

        if (fragment.Length == 1)
        {
            width = legacyWidth; // legacy fragment with no width suffix
            return true;
        }

        return int.TryParse(fragment.AsSpan(1), out width) && width > 0;
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
