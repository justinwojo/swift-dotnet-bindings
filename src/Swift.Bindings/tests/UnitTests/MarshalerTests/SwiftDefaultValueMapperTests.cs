#nullable enable
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftDefaultValueMapperTests
{
    #region Nil mapping

    [Fact]
    public void Nil_OptionalType_ReturnsNull()
    {
        var typeSpec = MakeOptionalType("Swift.Int");
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("nil", typeSpec, new EmptyTypeDatabase());
        Assert.Equal("null", result);
    }

    [Fact]
    public void Nil_ClassType_ReturnsNull()
    {
        var typeSpec = MakeNamedType("TestModule.MyClass");
        var db = new SimpleTypeDatabase(("TestModule.MyClass", TypeRecordKind.Class, TypeRecordFlags.RequiresMemoryManagement, "TestModule", "MyClass"));
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("nil", typeSpec, db);
        Assert.Equal("null", result);
    }

    [Fact]
    public void Nil_ValueType_ReturnsDefault()
    {
        var typeSpec = MakeNamedType("Swift.Int");
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("nil", typeSpec, new EmptyTypeDatabase());
        Assert.Equal("default", result);
    }

    #endregion

    #region Bool literals

    [Fact]
    public void True_ReturnsTrue()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("true", MakeNamedType("Swift.Bool"), new EmptyTypeDatabase());
        Assert.Equal("true", result);
    }

    [Fact]
    public void False_ReturnsFalse()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("false", MakeNamedType("Swift.Bool"), new EmptyTypeDatabase());
        Assert.Equal("false", result);
    }

    #endregion

    #region Integer literals

    [Fact]
    public void PositiveInt_ReturnsSame()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("10", MakeNamedType("Swift.Int"), new EmptyTypeDatabase());
        Assert.Equal("10", result);
    }

    [Fact]
    public void Zero_ReturnsSame()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("0", MakeNamedType("Swift.Int"), new EmptyTypeDatabase());
        Assert.Equal("0", result);
    }

    [Fact]
    public void NegativeInt_ReturnsSame()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("-1", MakeNamedType("Swift.Int"), new EmptyTypeDatabase());
        Assert.Equal("-1", result);
    }

    [Fact]
    public void UnderscoreInt_StripsUnderscores()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("1_000_000", MakeNamedType("Swift.Int"), new EmptyTypeDatabase());
        Assert.Equal("1000000", result);
    }

    #endregion

    #region Float literals

    [Fact]
    public void Double_ReturnsSame()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("0.02", MakeNamedType("Swift.Double"), new EmptyTypeDatabase());
        Assert.Equal("0.02", result);
    }

    [Fact]
    public void Float_AddsFSuffix()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("0.8", MakeNamedType("Swift.Float"), new EmptyTypeDatabase());
        Assert.Equal("0.8f", result);
    }

    [Fact]
    public void Float_UnderscoreStripped()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("1_000.5", MakeNamedType("Swift.Double"), new EmptyTypeDatabase());
        Assert.Equal("1000.5", result);
    }

    #endregion

    #region String literals

    [Fact]
    public void String_ReturnsSame()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("\"Hello\"", MakeNamedType("Swift.String"), new EmptyTypeDatabase());
        Assert.Equal("\"Hello\"", result);
    }

    [Fact]
    public void EmptyString_ReturnsSame()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("\"\"", MakeNamedType("Swift.String"), new EmptyTypeDatabase());
        Assert.Equal("\"\"", result);
    }

    #endregion

    #region Enum dot syntax

    [Fact]
    public void DotCase_SimpleEnum_ReturnsMapped()
    {
        var db = new SimpleTypeDatabase(
            ("TestModule.Level", TypeRecordKind.Enum, TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, "TestModule", "Level"));
        var typeSpec = MakeNamedType("TestModule.Level");
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault(".mid", typeSpec, db);
        Assert.Equal("TestModule.Level.Mid", result);
    }

    [Fact]
    public void DotCase_OptionalEnum_UnwrapsOptional()
    {
        var db = new SimpleTypeDatabase(
            ("TestModule.Level", TypeRecordKind.Enum, TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, "TestModule", "Level"));
        var typeSpec = MakeOptionalType("TestModule.Level");
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault(".high", typeSpec, db);
        Assert.Equal("TestModule.Level.High", result);
    }

    [Fact]
    public void DotCase_NotSimpleEnum_ReturnsNull()
    {
        var db = new SimpleTypeDatabase(
            ("TestModule.Shape", TypeRecordKind.Enum, TypeRecordFlags.Frozen, "TestModule", "Shape"));
        var typeSpec = MakeNamedType("TestModule.Shape");
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault(".circle", typeSpec, db);
        Assert.Null(result);
    }

    [Fact]
    public void DotNone_Optional_ReturnsNull()
    {
        var typeSpec = MakeOptionalType("Swift.Int");
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault(".none", typeSpec, new EmptyTypeDatabase());
        Assert.Equal("null", result);
    }

    #endregion

    #region Qualified enum

    [Fact]
    public void QualifiedEnum_UnqualifiedTypeName_ResolvesViaParamType()
    {
        var db = new SimpleTypeDatabase(
            ("SVGView.SVGColor", TypeRecordKind.Enum, TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, "SVGView", "SVGColor"));
        var typeSpec = MakeNamedType("SVGView.SVGColor");
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("SVGColor.black", typeSpec, db);
        // "SVGColor" alone is unqualified, but the fallback resolves via paramTypeSpec
        Assert.Equal("SVGView.SVGColor.Black", result);
    }

    [Fact]
    public void QualifiedEnum_FullyQualified_ReturnsMapped()
    {
        var db = new SimpleTypeDatabase(
            ("SVGView.SVGColor", TypeRecordKind.Enum, TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, "SVGView", "SVGColor"));
        var typeSpec = MakeNamedType("SVGView.SVGColor");
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("SVGView.SVGColor.black", typeSpec, db);
        // "SVGView.SVGColor" is "SVGView.SVGColor" → splits as type="SVGView.SVGColor", case="black"
        // Wait — expr has two dots: "SVGView.SVGColor.black". LastIndexOf('.') gives "SVGView.SVGColor" + "black"
        Assert.Equal("SVGView.SVGColor.Black", result);
    }

    [Fact]
    public void QualifiedEnum_UnqualifiedOptionalParam_ResolvesViaParamType()
    {
        var db = new SimpleTypeDatabase(
            ("SVGView.SVGColor", TypeRecordKind.Enum, TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, "SVGView", "SVGColor"));
        var typeSpec = MakeOptionalType("SVGView.SVGColor");
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("SVGColor.black", typeSpec, db);
        Assert.Equal("SVGView.SVGColor.Black", result);
    }

    [Fact]
    public void QualifiedEnum_UnqualifiedNonEnum_ReturnsNull()
    {
        // When paramTypeSpec resolves to a non-enum, should return null
        var db = new SimpleTypeDatabase(
            ("TestModule.Config", TypeRecordKind.Struct, TypeRecordFlags.Frozen, "TestModule", "Config"));
        var typeSpec = MakeNamedType("TestModule.Config");
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("Config.default", typeSpec, db);
        Assert.Null(result);
    }

    [Fact]
    public void QualifiedEnum_PropertyChain_ReturnsNull()
    {
        // "LottieConfiguration.shared.decodingStrategy" is a property chain, not an enum case.
        // The fallback must not misinterpret it as an enum case via paramTypeSpec.
        var db = new SimpleTypeDatabase(
            ("Lottie.DecodingStrategy", TypeRecordKind.Enum, TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, "Lottie", "DecodingStrategy"));
        var typeSpec = MakeNamedType("Lottie.DecodingStrategy");
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("LottieConfiguration.shared.decodingStrategy", typeSpec, db);
        Assert.Null(result);
    }

    #endregion

    #region Unmappable expressions

    [Fact]
    public void StructConstructor_ReturnsNull()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("Config()", MakeNamedType("TestModule.Config"), new EmptyTypeDatabase());
        Assert.Null(result);
    }

    [Fact]
    public void StaticProperty_ReturnsNull()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("Config.default", MakeNamedType("TestModule.Config"), new EmptyTypeDatabase());
        // "Config.default" has a dot but "Config" is unqualified → TryLookupTypeRecord fails → null
        Assert.Null(result);
    }

    [Fact]
    public void EmptyArray_ReturnsNull()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("[]", MakeNamedType("Swift.Array"), new EmptyTypeDatabase());
        Assert.Null(result);
    }

    [Fact]
    public void DictLiteral_ReturnsNull()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("[:]", MakeNamedType("Swift.Dictionary"), new EmptyTypeDatabase());
        Assert.Null(result);
    }

    [Fact]
    public void FunctionCall_ReturnsNull()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("makeDefault()", MakeNamedType("TestModule.Config"), new EmptyTypeDatabase());
        Assert.Null(result);
    }

    [Fact]
    public void EmptyString_Expression_ReturnsNull()
    {
        var result = SwiftDefaultValueMapper.TryMapToCSharpDefault("", MakeNamedType("Swift.Int"), new EmptyTypeDatabase());
        Assert.Null(result);
    }

    #endregion

    #region Helpers

    private static NamedTypeSpec MakeNamedType(string name)
    {
        return new NamedTypeSpec(name);
    }

    private static NamedTypeSpec MakeOptionalType(string innerName)
    {
        var inner = new NamedTypeSpec(innerName);
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(inner);
        return optional;
    }

    private class EmptyTypeDatabase : ITypeDatabase
    {
        public string? AsyncLibraryName => null;
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            record = null;
            return false;
        }
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    private class SimpleTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types = new();
        public string? AsyncLibraryName => null;

        public SimpleTypeDatabase(params (string SwiftName, TypeRecordKind Kind, TypeRecordFlags Flags, string CSharpNs, string CSharpName)[] types)
        {
            foreach (var (swiftName, kind, flags, csNs, csName) in types)
            {
                _types[swiftName] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csNs, csName),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftName),
                    MetadataAccessor = "",
                    Flags = flags,
                    Kind = kind
                };
            }
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _types.ContainsKey(swiftTypeName.ModuleQualifiedName);
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
            => _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
