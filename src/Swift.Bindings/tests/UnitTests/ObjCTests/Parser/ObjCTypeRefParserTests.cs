// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

public class ObjCTypeRefParserTests
{
    [Fact]
    public void Parse_NSStringPointer_ReturnsPointerType()
    {
        var result = ObjCTypeRefParser.Parse("NSString *");
        Assert.Equal("NSString", result.Name);
        Assert.True(result.IsPointer);
        Assert.Equal(ObjCNullability.Unspecified, result.Nullability);
    }

    [Fact]
    public void Parse_NSStringPointerNullable_ReturnsNullable()
    {
        var result = ObjCTypeRefParser.Parse("NSString * _Nullable");
        Assert.Equal("NSString", result.Name);
        Assert.True(result.IsPointer);
        Assert.Equal(ObjCNullability.Nullable, result.Nullability);
    }

    [Fact]
    public void Parse_NSStringPointerNonnull_ReturnsNonnull()
    {
        var result = ObjCTypeRefParser.Parse("NSString * _Nonnull");
        Assert.Equal("NSString", result.Name);
        Assert.True(result.IsPointer);
        Assert.Equal(ObjCNullability.Nonnull, result.Nullability);
    }

    [Fact]
    public void Parse_BOOL_ReturnsPrimitive()
    {
        var result = ObjCTypeRefParser.Parse("BOOL");
        Assert.Equal("BOOL", result.Name);
        Assert.False(result.IsPointer);
        Assert.False(result.IsBlock);
    }

    [Fact]
    public void Parse_Void_ReturnsVoidType()
    {
        var result = ObjCTypeRefParser.Parse("void");
        Assert.Equal("void", result.Name);
        Assert.False(result.IsPointer);
    }

    [Fact]
    public void Parse_IdWithProtocol_ReturnsProtocolQualification()
    {
        var result = ObjCTypeRefParser.Parse("id<NSCoding>");
        Assert.Equal("id", result.Name);
        Assert.True(result.IsPointer);
        Assert.Equal("NSCoding", result.ProtocolQualification);
    }

    [Fact]
    public void Parse_BlockType_ReturnsBlockWithParams()
    {
        var result = ObjCTypeRefParser.Parse("void (^)(NSString *)");
        Assert.True(result.IsBlock);
        Assert.NotNull(result.BlockReturnType);
        Assert.Equal("void", result.BlockReturnType!.Name);
        Assert.Single(result.BlockParams);
        Assert.Equal("NSString", result.BlockParams[0].Name);
        Assert.True(result.BlockParams[0].IsPointer);
    }

    [Fact]
    public void Parse_BlockTypeNoParams_ReturnsBlockWithEmptyParams()
    {
        var result = ObjCTypeRefParser.Parse("void (^)(void)");
        Assert.True(result.IsBlock);
        Assert.NotNull(result.BlockReturnType);
        Assert.Equal("void", result.BlockReturnType!.Name);
        Assert.Empty(result.BlockParams);
    }

    [Fact]
    public void Parse_DoublePointer_ReturnsPointeeType()
    {
        var result = ObjCTypeRefParser.Parse("NSError **");
        Assert.Equal("NSError", result.Name);
        Assert.True(result.IsPointer);
        Assert.NotNull(result.PointeeType);
        Assert.Equal("NSError", result.PointeeType!.Name);
        Assert.True(result.PointeeType.IsPointer);
    }

    [Fact]
    public void Parse_GenericArrayType_ReturnsGenericArgs()
    {
        var result = ObjCTypeRefParser.Parse("NSArray<NSString *> *");
        Assert.Equal("NSArray", result.Name);
        Assert.True(result.IsPointer);
        Assert.Single(result.GenericArgs);
        Assert.Equal("NSString", result.GenericArgs[0].Name);
        Assert.True(result.GenericArgs[0].IsPointer);
    }

    [Fact]
    public void Parse_IntType_ReturnsPrimitive()
    {
        var result = ObjCTypeRefParser.Parse("int");
        Assert.Equal("int", result.Name);
        Assert.False(result.IsPointer);
    }

    [Fact]
    public void Parse_UnderscoreNullableAnnotation_Recognized()
    {
        var result = ObjCTypeRefParser.Parse("NSString * __nullable");
        Assert.Equal("NSString", result.Name);
        Assert.True(result.IsPointer);
        Assert.Equal(ObjCNullability.Nullable, result.Nullability);
    }

    [Fact]
    public void Parse_UnderscoreNonnullAnnotation_Recognized()
    {
        var result = ObjCTypeRefParser.Parse("NSString * __nonnull");
        Assert.Equal("NSString", result.Name);
        Assert.True(result.IsPointer);
        Assert.Equal(ObjCNullability.Nonnull, result.Nullability);
    }

    [Fact]
    public void Parse_EnumQualType_StripsEnumKeyword()
    {
        var result = ObjCTypeRefParser.Parse("enum BRLMPrinterModel");
        Assert.Equal("BRLMPrinterModel", result.Name);
        Assert.False(result.IsPointer);
    }

    [Fact]
    public void Parse_StructQualType_StripsStructKeyword()
    {
        var result = ObjCTypeRefParser.Parse("struct CGRect");
        Assert.Equal("CGRect", result.Name);
        Assert.False(result.IsPointer);
    }
}
