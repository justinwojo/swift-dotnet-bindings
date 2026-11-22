// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the single oracle that decides what a simple enum's value is when it crosses the
/// <c>@_cdecl</c> boundary: <see cref="CdeclParamMapper.HasCdeclScalarRawValue"/> and
/// <see cref="CdeclParamMapper.GetCdeclEnumTransportType"/>.
/// <para>
/// Three emitters read this — the wire type in <c>CdeclReturnMapping.Classify</c>, the conversion
/// in <c>CdeclReturnRenderer</c>, and the closure callback shape in <c>ClosureHandler</c>. When
/// they disagree, the wrapper declares one type and hands back another, which either fails to
/// compile (and silently withdraws the member) or reinterprets the bytes. So the rule is asserted
/// here directly rather than only through each consumer.
/// </para>
/// </summary>
public class CdeclEnumTransportTests
{
    [Theory]
    [InlineData("Swift.Int")]
    [InlineData("Int")]
    [InlineData("Swift.UInt")]
    [InlineData("Swift.Int8")]
    [InlineData("UInt8")]
    [InlineData("Swift.Int16")]
    [InlineData("UInt16")]
    [InlineData("Swift.Int32")]
    [InlineData("Int32")]
    [InlineData("Swift.UInt32")]
    [InlineData("Swift.Int64")]
    [InlineData("UInt64")]
    public void IntegralRawValue_CrossesAsItsOwnRawValue(string rawValueTypeName)
    {
        Assert.True(CdeclParamMapper.HasCdeclScalarRawValue(rawValueTypeName));
        Assert.Equal(CdeclParamMapper.GetSwiftRawValueType(rawValueTypeName),
            CdeclParamMapper.GetCdeclEnumTransportType(rawValueTypeName));
    }

    /// <summary>
    /// A raw value that is not a C-ABI integer is not what crosses: the managed enum carries the
    /// case ordinal with an <c>int</c> underlying type, so the wrapper must transport that ordinal.
    /// <c>String</c> appears in both spellings because the module-qualified one is what the parser
    /// actually produces, and an unqualified-only exclusion is how this rule was first missed.
    /// </summary>
    [Theory]
    [InlineData("Swift.String")]
    [InlineData("String")]
    [InlineData("Swift.Bool")]
    [InlineData("Bool")]
    [InlineData("Swift.Double")]
    [InlineData("Double")]
    [InlineData("Swift.Float")]
    [InlineData("Float")]
    public void NonIntegralRawValue_CrossesAsTheCaseOrdinal(string rawValueTypeName)
    {
        Assert.False(CdeclParamMapper.HasCdeclScalarRawValue(rawValueTypeName));

        var expected = EnumHandler.GetSwiftScalarType(
            EnumHandler.GetCSharpEnumUnderlyingType(rawValueTypeName));
        Assert.Equal(expected, CdeclParamMapper.GetCdeclEnumTransportType(rawValueTypeName));
    }

    /// <summary>A tag-only enum has no raw value at all and also crosses as the case ordinal.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoRawValue_CrossesAsTheCaseOrdinal(string? rawValueTypeName)
    {
        Assert.False(CdeclParamMapper.HasCdeclScalarRawValue(rawValueTypeName));

        var expected = EnumHandler.GetSwiftScalarType(
            EnumHandler.GetCSharpEnumUnderlyingType(rawValueTypeName));
        Assert.Equal(expected, CdeclParamMapper.GetCdeclEnumTransportType(rawValueTypeName));
    }

    /// <summary>
    /// The transport type must be integer-literal expressible for every input, because the
    /// throwing wrapper's catch block spells its sentinel as <c>{transport}(0)</c>.
    /// </summary>
    [Theory]
    [InlineData("Swift.String")]
    [InlineData("Swift.Bool")]
    [InlineData("Swift.Double")]
    [InlineData("Swift.Int32")]
    [InlineData(null)]
    public void TransportType_IsAlwaysAnIntegerType(string? rawValueTypeName)
    {
        var transport = CdeclParamMapper.GetCdeclEnumTransportType(rawValueTypeName);
        Assert.Contains(transport, new[]
        {
            "Swift.Int", "Swift.UInt", "Swift.Int8", "Swift.UInt8", "Swift.Int16", "Swift.UInt16",
            "Swift.Int32", "Swift.UInt32", "Swift.Int64", "Swift.UInt64"
        });
    }
    [Theory]
    [InlineData("Int", false, true, true)]
    [InlineData("Swift.Int32", false, true, true)]
    [InlineData("Int", true, true, false)]
    [InlineData("String", false, true, false)]
    [InlineData(null, false, true, false)]
    // A record demoted to a class projection passes `.RawValue`, whatever its tag flag says.
    [InlineData("Int", true, false, true)]
    public void CrossesAsRawValue_OnlyForAnIntegralRawValueNotCrossingAsTheTag(
        string? rawValueTypeName, bool crossesAsCaseTag, bool simpleEnum, bool expected)
    {
        var record = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("M", "E"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("M.E"),
            MetadataAccessor = "$s1M1EOMa",
            Flags = simpleEnum ? TypeRecordFlags.SimpleEnum : TypeRecordFlags.None,
            Kind = TypeRecordKind.Enum,
            RawValueTypeName = rawValueTypeName,
            RawValueCrossesAsCaseTag = crossesAsCaseTag
        };
        Assert.Equal(expected, SimpleEnumCrossing.CrossesAsRawValue(record));
    }

    [Fact]
    public void ScalarToOptionalCase_RawValueUsesFailableInit_TagLoadsTheCaseOnlyInRange()
    {
        Assert.Equal("M.E(rawValue: v)", SimpleEnumCrossing.ScalarToOptionalCase(true, 0, "M.E", "v"));
        var tag = SimpleEnumCrossing.ScalarToOptionalCase(false, 3, "M.E", "v");
        // A tag outside the enum's cases names no case: the site gets nil, as init(rawValue:) gives it.
        Assert.Contains("..<3).contains(Swift.Int64(truncatingIfNeeded: v))", tag);
        Assert.Contains("Swift.Optional<M.E>.some(", tag);
        Assert.Contains("load(as: M.E.self)", tag);
        Assert.EndsWith(": Swift.Optional<M.E>.none", tag);
        Assert.DoesNotContain("rawValue", tag);
        // A tag-crossing enum with no known case count cannot be range-checked, so emission refuses it.
        Assert.Throws<InvalidOperationException>(() => SimpleEnumCrossing.ScalarToOptionalCase(false, 0, "M.E", "v"));
    }

    [Fact]
    public void CaseToScalar_RawValueConverts_TagCopiesTheEnumBytes()
    {
        Assert.Equal("Int64((x).rawValue)", SimpleEnumCrossing.CaseToScalar(true, "M.E", "Int64", "x"));
        var tag = SimpleEnumCrossing.CaseToScalar(false, "M.E", "Int64", "x");
        Assert.Contains("var __sbTag: Int64 = 0", tag);
        Assert.Contains("byteCount: Swift.MemoryLayout<M.E>.size", tag);
        Assert.DoesNotContain("rawValue", tag);
    }
}
