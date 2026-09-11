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
            "Int", "UInt", "Int8", "UInt8", "Int16", "UInt16",
            "Int32", "UInt32", "Int64", "UInt64"
        });
    }
}
