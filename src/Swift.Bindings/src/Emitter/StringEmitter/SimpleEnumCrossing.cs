// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The one mapping between a simple enum's C# constant and its Swift case, shared by every site
/// that hands such a value across the native boundary in either direction (arguments, returns,
/// properties, subscripts, closures and callbacks, extension trampolines, the SwiftUI bridge).
/// <para>
/// A value crosses either as Swift's <c>.rawValue</c> / <c>init(rawValue:)</c> or as the case tag,
/// the enum's in-memory value. Which one is a property of the enum, decided once
/// (<see cref="CrossesAsRawValue"/>): the raw value only where it is known for certain and is the
/// in-memory value too (an <c>@objc</c> enum, an ObjC NS_ENUM, an Apple enum synthesized from the
/// managed surface); the case tag for every other simple enum, including a plain Swift
/// <c>enum E: Int</c> whose raw values its interface does not print. The C# constant is that same
/// scalar (<see cref="EnumDecl.GetCaseMarshalScalar"/>), so the container and Optional paths, which
/// read the in-memory value, agree with the scalar crossings by construction.
/// </para>
/// </summary>
internal static class SimpleEnumCrossing
{
    /// <summary>
    /// Whether a simple enum's value crosses as Swift's <c>.rawValue</c>. False for an enum with no
    /// C-scalar raw value (no raw value, or a Bool, floating-point or String one) and for an integral
    /// enum whose record says its values cross as the case tag. A record no longer a simple enum (one
    /// demoted to a class projection, which passes <c>.RawValue</c>) crosses as the raw value.
    /// </summary>
    public static bool CrossesAsRawValue(TypeRecord record)
        => CdeclParamMapper.HasCdeclScalarRawValue(record.RawValueTypeName)
           && !(record.RawValueCrossesAsCaseTag && record.Flags.HasFlag(TypeRecordFlags.SimpleEnum));

    /// <summary>
    /// A Swift expression of type <paramref name="swiftEnumType"/>: the case whose tag is the integer
    /// <paramref name="scalarExpr"/>. Reads the tag from the scalar's low bytes (little-endian on every
    /// Apple target), the inverse of <see cref="CaseToTag"/>; the enum is never wider than the scalar.
    /// </summary>
    public static string TagToCase(string swiftEnumType, string scalarExpr)
        => $"{{ () -> {swiftEnumType} in var __sbTag = {scalarExpr}; "
         + $"return Swift.withUnsafeMutablePointer(to: &__sbTag) {{ __sbTagPtr in "
         + $"Swift.UnsafeMutableRawPointer(__sbTagPtr).load(as: {swiftEnumType}.self) }} }}()";

    /// <summary>
    /// The number of case tags a record's values cross as (<see cref="TypeRecord.CaseTagCount"/>), or 0
    /// when they cross as the raw value.
    /// </summary>
    public static int CaseTagCount(TypeRecord record)
        => CrossesAsRawValue(record) ? 0 : record.CaseTagCount;

    /// <summary>
    /// A Swift expression of type <c>Optional&lt;<paramref name="swiftEnumType"/>&gt;</c> for a site that
    /// binds the reconstructed case with <c>guard let</c>, nil for a value that names no case:
    /// <c>init(rawValue:)</c> when the value crosses as the raw value, and otherwise the case whose tag
    /// it is, after checking it is one of the enum's <paramref name="caseTagCount"/> tags (loading any
    /// other integer as the enum is undefined).
    /// </summary>
    public static string ScalarToOptionalCase(
        bool crossesAsRawValue, int caseTagCount, string swiftEnumType, string scalarExpr)
    {
        if (crossesAsRawValue)
            return $"{swiftEnumType}(rawValue: {scalarExpr})";
        if (caseTagCount <= 0)
            throw new System.InvalidOperationException(
                $"Simple enum '{swiftEnumType}' crosses as its case tag but its case count is unknown.");
        return $"((0 as Swift.Int64)..<{caseTagCount}).contains(Swift.Int64(truncatingIfNeeded: {scalarExpr})) "
             + $"? Swift.Optional<{swiftEnumType}>.some({TagToCase(swiftEnumType, scalarExpr)}) "
             + $": Swift.Optional<{swiftEnumType}>.none";
    }

    /// <summary>
    /// A Swift expression of type <paramref name="scalarType"/>: the case tag of
    /// <paramref name="valueExpr"/>, copied into a zero-initialised scalar.
    /// </summary>
    public static string CaseToTag(string swiftEnumType, string scalarType, string valueExpr)
        => $"{{ () -> {scalarType} in var __sbTag: {scalarType} = 0; var __sbCase: {swiftEnumType} = {valueExpr}; "
         + $"Swift.withUnsafeMutablePointer(to: &__sbTag) {{ __sbDst in Swift.withUnsafePointer(to: &__sbCase) {{ __sbSrc in "
         + $"Swift.UnsafeMutableRawPointer(__sbDst).copyMemory(from: Swift.UnsafeRawPointer(__sbSrc), "
         + $"byteCount: Swift.MemoryLayout<{swiftEnumType}>.size) }} }}; return __sbTag }}()";

    /// <summary>
    /// A Swift expression of type <paramref name="scalarType"/> for <paramref name="valueExpr"/>:
    /// its <c>.rawValue</c> converted to the scalar when the value crosses as the raw value, its case
    /// tag otherwise.
    /// </summary>
    public static string CaseToScalar(
        bool crossesAsRawValue, string swiftEnumType, string scalarType, string valueExpr)
        => crossesAsRawValue
            ? $"{scalarType}(({valueExpr}).rawValue)"
            : CaseToTag(swiftEnumType, scalarType, valueExpr);
}
