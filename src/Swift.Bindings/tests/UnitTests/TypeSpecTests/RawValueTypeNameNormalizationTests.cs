// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the "RawValueTypeName is always unqualified" invariant. Enum raw value types are
/// stdlib types emitted unqualified by the Swift ABI digester, and a family of bare-only
/// classification switches (EnumDecl.IsIntegralRawValue / IsStringRawValue, the SwiftUI
/// MapEnumRawValueType bridge, assorted direct == "String"/"Int32" checks) depend on that
/// spelling to choose the enum's emission strategy and public surface. The two carriers
/// (EnumDecl and TypeRecord) normalize on assignment so a qualified spelling from any
/// registration source (XML re-read, cross-module pre-registration, hand-written records)
/// cannot silently mis-route classification.
/// </summary>
public class RawValueTypeNameNormalizationTests
{
    [Theory]
    [InlineData("Swift.Int32", "Int32")]
    [InlineData("Swift.Int", "Int")]
    [InlineData("Swift.UInt64", "UInt64")]
    [InlineData("Swift.String", "String")]
    [InlineData("Int32", "Int32")]      // already bare — unchanged
    [InlineData("String", "String")]    // already bare — unchanged
    [InlineData(null, null)]
    [InlineData("", "")]
    public void NormalizeRawValueTypeName_StripsSwiftPrefixOnly(string? input, string? expected)
    {
        Assert.Equal(expected, TypeSpecHelpers.NormalizeRawValueTypeName(input));
    }

    [Fact]
    public void NormalizeRawValueTypeName_NonSwiftQualified_LeftUnchanged()
    {
        // Only the stdlib "Swift." prefix is stripped; an unexpected foreign qualifier is
        // preserved verbatim rather than silently truncated to a misleading bare name.
        Assert.Equal("MyModule.Weird", TypeSpecHelpers.NormalizeRawValueTypeName("MyModule.Weird"));
    }

    [Fact]
    public void EnumDecl_QualifiedRawValue_NormalizesAndClassifiesAsSimpleEnum()
    {
        // A qualified raw spelling slipping in must still read back bare AND classify as a
        // simple (integral-raw) enum — without normalization IsIntegralRawValue's bare-only
        // switch would return false and the enum would degrade to the heavier opaque path.
        var enumDecl = CreateEnumDecl("Status");
        enumDecl.RawValueTypeName = "Swift.Int32";

        Assert.Equal("Int32", enumDecl.RawValueTypeName);
        Assert.True(enumDecl.IsRawRepresentable);
        Assert.True(enumDecl.IsSimpleEnum);
    }

    [Fact]
    public void EnumDecl_QualifiedStringRawValue_NormalizesIsStringRawValue()
    {
        var enumDecl = CreateEnumDecl("Mode");
        enumDecl.RawValueTypeName = "Swift.String";

        Assert.Equal("String", enumDecl.RawValueTypeName);
        Assert.True(enumDecl.IsStringRawValue);
    }

    [Fact]
    public void TypeRecord_QualifiedRawValue_NormalizesOnInit()
    {
        var record = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Status"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Status"),
            MetadataAccessor = "$s10TestModule6StatusOMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Enum,
            RawValueTypeName = "Swift.Int32",
        };

        Assert.Equal("Int32", record.RawValueTypeName);
    }

    private static EnumDecl CreateEnumDecl(string name)
    {
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}ON",
            MetadataAccessor = $"$s10TestModule{name.Length}{name}OMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Cases = new List<EnumCaseDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
        };
    }
}
