// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the ABI width-consistency contract between the two enum-tag transport helpers:
/// <see cref="EnumHandler.GetCSharpEnumUnderlyingType"/> (the C# P/Invoke parameter/return
/// type) and <see cref="CdeclParamMapper.GetSwiftRawValueType"/> (the Swift <c>@_cdecl</c>
/// wrapper parameter/return type). For the SAME enum raw-value-type input, both sides must
/// describe a scalar of the SAME byte width — otherwise the C# caller and the Swift wrapper
/// disagree on the size of the value crossing the boundary.
///
/// Regression guard for the latent <c>int</c>↔<c>Int</c> mismatch on tag-only (no-raw-value)
/// enums: C# emitted 32-bit <c>int</c> while the Swift wrapper declared pointer-width <c>Int</c>
/// (64-bit). It only survived because arm64 zero-extends a 32-bit argument register into the
/// 64-bit one; on a spilled argument or a differently-sized convention it would corrupt the
/// call frame. The two helpers must agree by construction.
/// </summary>
public class EnumAbiWidthConsistencyTests
{
    // Byte widths for the C# scalar names GetCSharpEnumUnderlyingType can produce.
    private static int CSharpScalarByteWidth(string csType) => csType switch
    {
        "sbyte" or "byte" => 1,
        "short" or "ushort" => 2,
        "int" or "uint" => 4,
        "long" or "ulong" => 8,
        // Pointer-width on the only targets (arm64 / x86_64).
        "nint" or "nuint" => 8,
        _ => throw new Xunit.Sdk.XunitException($"Unexpected C# scalar '{csType}' — extend the width map."),
    };

    // Byte widths for the Swift scalar names GetSwiftRawValueType can produce for integral inputs.
    private static int SwiftScalarByteWidth(string swiftType) => swiftType switch
    {
        "Int8" or "UInt8" => 1,
        "Int16" or "UInt16" => 2,
        "Int32" or "UInt32" => 4,
        "Int64" or "UInt64" => 8,
        // Swift.Int / Swift.UInt are pointer-width (64-bit on arm64 / x86_64).
        "Int" or "UInt" => 8,
        _ => throw new Xunit.Sdk.XunitException($"Unexpected Swift scalar '{swiftType}' — extend the width map."),
    };

    [Theory]
    // Explicit integral raw-value types — already consistent, kept as a guard.
    [InlineData("Int8")]
    [InlineData("UInt8")]
    [InlineData("Int16")]
    [InlineData("UInt16")]
    [InlineData("Int32")]
    [InlineData("UInt32")]
    [InlineData("Int64")]
    [InlineData("UInt64")]
    [InlineData("Int")]
    [InlineData("UInt")]
    // Module-qualified forms of the SAME known integral types. GetSwiftRawValueType already
    // accepts both "Swift.Int64" and "Int64"; GetCSharpEnumUnderlyingType must too, or a
    // qualified raw-value name silently falls through to C# "int" (4) while the Swift wrapper
    // emits the correct width (e.g. Int64 = 8) — the same width-disagreement class as the
    // tag-only defect, just reached through the qualified spelling. Both mappers must agree
    // across the ENTIRE domain one of them recognizes.
    [InlineData("Swift.Int8")]
    [InlineData("Swift.UInt8")]
    [InlineData("Swift.Int16")]
    [InlineData("Swift.UInt16")]
    [InlineData("Swift.Int32")]
    [InlineData("Swift.UInt32")]
    [InlineData("Swift.Int64")]
    [InlineData("Swift.UInt64")]
    [InlineData("Swift.Int")]
    [InlineData("Swift.UInt")]
    // Tag-only enums (no raw value) — the defect: C# "int" (4) vs Swift "Int" (8).
    [InlineData(null)]
    [InlineData("")]
    // An unrecognized raw-value type name must also degrade to the same width on both sides
    // (both helpers fall back to a 32-bit int).
    [InlineData("Some.Unrecognized.RawType")]
    public void EnumTransportWidth_SwiftAndCSharpAgree(string? rawValueTypeName)
    {
        var csType = EnumHandler.GetCSharpEnumUnderlyingType(rawValueTypeName);
        var swiftType = CdeclParamMapper.GetSwiftRawValueType(rawValueTypeName);

        Assert.Equal(CSharpScalarByteWidth(csType), SwiftScalarByteWidth(swiftType));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TagOnlyEnum_SwiftWrapperUsesInt32_MatchingCSharpInt(string? rawValueTypeName)
    {
        // The C# enum declaration / P/Invoke uses 32-bit `int` for a no-raw-value enum.
        Assert.Equal("int", EnumHandler.GetCSharpEnumUnderlyingType(rawValueTypeName));
        // The Swift @_cdecl transport scalar must be the matching 32-bit Int32 — never
        // pointer-width Int (the latent width mismatch).
        Assert.Equal("Int32", CdeclParamMapper.GetSwiftRawValueType(rawValueTypeName));
    }

    // The tuple-element metadata mapper (GetSwiftAbiMetadataType) feeds TypeMetadata construction
    // for an enum raw value used as a tuple element; its C# metadata type's width MUST equal the
    // Swift ABI width of the same raw value. Like the transport mappers it must accept BOTH the
    // qualified and unqualified spelling — its unknown fallback is pointer-width `nint`, so a
    // qualified small-int slipping through would over-size the element metadata. Covers only KNOWN
    // integral types in both spellings; null/""/unrecognized are intentionally excluded because the
    // metadata mapper uses the NSInteger (pointer-width) convention there while the cdecl tag
    // transport uses Int32 — a deliberately different contract for the no-raw-value case.
    [Theory]
    [InlineData("Int8")]
    [InlineData("UInt8")]
    [InlineData("Int16")]
    [InlineData("UInt16")]
    [InlineData("Int32")]
    [InlineData("UInt32")]
    [InlineData("Int64")]
    [InlineData("UInt64")]
    [InlineData("Int")]
    [InlineData("UInt")]
    [InlineData("Swift.Int8")]
    [InlineData("Swift.UInt8")]
    [InlineData("Swift.Int16")]
    [InlineData("Swift.UInt16")]
    [InlineData("Swift.Int32")]
    [InlineData("Swift.UInt32")]
    [InlineData("Swift.Int64")]
    [InlineData("Swift.UInt64")]
    [InlineData("Swift.Int")]
    [InlineData("Swift.UInt")]
    public void TupleElementMetadataWidth_MatchesSwiftRawValueWidth(string rawValueTypeName)
    {
        var metadataType = EnumHandler.GetSwiftAbiMetadataType(rawValueTypeName);
        var swiftType = CdeclParamMapper.GetSwiftRawValueType(rawValueTypeName);

        Assert.Equal(SwiftScalarByteWidth(swiftType), CSharpScalarByteWidth(metadataType));
    }
}
