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
        Assert.Single(result.ProtocolQualifications);
        Assert.Equal("NSCoding", result.ProtocolQualifications[0]);
    }

    [Fact]
    public void Parse_IdWithMultipleProtocols_ReturnsAllProtocols()
    {
        var result = ObjCTypeRefParser.Parse("id<NSCoding, NSCopying>");
        Assert.Equal("id", result.Name);
        Assert.True(result.IsPointer);
        Assert.Equal(2, result.ProtocolQualifications.Count);
        Assert.Equal("NSCoding", result.ProtocolQualifications[0]);
        Assert.Equal("NSCopying", result.ProtocolQualifications[1]);
    }

    [Fact]
    public void Parse_ConcreteTypeWithProtocol_ReturnsProtocolQualification()
    {
        var result = ObjCTypeRefParser.Parse("NSObject<NSCopying> *");
        Assert.Equal("NSObject", result.Name);
        Assert.True(result.IsPointer);
        Assert.Single(result.ProtocolQualifications);
        Assert.Equal("NSCopying", result.ProtocolQualifications[0]);
        Assert.Empty(result.GenericArgs);
    }

    [Fact]
    public void Parse_ConcreteTypeWithMultipleProtocols_ReturnsAllProtocols()
    {
        var result = ObjCTypeRefParser.Parse("NSObject<NSCopying, NSSecureCoding> *");
        Assert.Equal("NSObject", result.Name);
        Assert.True(result.IsPointer);
        Assert.Equal(2, result.ProtocolQualifications.Count);
        Assert.Equal("NSCopying", result.ProtocolQualifications[0]);
        Assert.Equal("NSSecureCoding", result.ProtocolQualifications[1]);
        Assert.Empty(result.GenericArgs);
    }

    [Fact]
    public void Parse_CustomGenericContainer_WithContext_ReturnsGenericArgs()
    {
        // When ClangAstParser discovers MOSResults has ObjCTypeParamDecl children,
        // it registers it as an additional generic container. The parser then treats
        // angle-bracket args as GenericArgs instead of ProtocolQualifications.
        ObjCTypeRefParser.SetAdditionalGenericContainers(new HashSet<string> { "MOSResults", "MOSArray" });
        try
        {
            var result = ObjCTypeRefParser.Parse("MOSResults<MOSObjectType> *");
            Assert.Equal("MOSResults", result.Name);
            Assert.True(result.IsPointer);
            Assert.Single(result.GenericArgs);
            Assert.Equal("MOSObjectType", result.GenericArgs[0].Name);
            Assert.Empty(result.ProtocolQualifications);
        }
        finally
        {
            ObjCTypeRefParser.SetAdditionalGenericContainers(null);
        }
    }

    [Fact]
    public void Parse_CustomGenericContainer_WithoutContext_FallsBackToProtocolQualifications()
    {
        // Without AST context, the parser conservatively treats simple-name args
        // as protocol qualifications. ClangAstParser provides context to fix this.
        var result = ObjCTypeRefParser.Parse("MOSResults<MOSObjectType> *");
        Assert.Equal("MOSResults", result.Name);
        Assert.True(result.IsPointer);
        Assert.Single(result.ProtocolQualifications);
        Assert.Equal("MOSObjectType", result.ProtocolQualifications[0]);
        Assert.Empty(result.GenericArgs);
    }

    [Fact]
    public void Parse_NSArrayGeneric_ReturnsGenericArgs_NotProtocols()
    {
        var result = ObjCTypeRefParser.Parse("NSArray<NSString *> *");
        Assert.Equal("NSArray", result.Name);
        Assert.True(result.IsPointer);
        Assert.Single(result.GenericArgs);
        Assert.Equal("NSString", result.GenericArgs[0].Name);
        Assert.Empty(result.ProtocolQualifications);
    }

    [Fact]
    public void Parse_NSDictionaryGeneric_ReturnsGenericArgs()
    {
        var result = ObjCTypeRefParser.Parse("NSDictionary<NSString *, NSNumber *> *");
        Assert.Equal("NSDictionary", result.Name);
        Assert.Equal(2, result.GenericArgs.Count);
        Assert.Empty(result.ProtocolQualifications);
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
    public void Parse_DoublePointerWithSpace_ReturnsPointeeType()
    {
        // After nullability stripping, NSError * _Nullable * becomes NSError * *
        var result = ObjCTypeRefParser.Parse("NSError * *");
        Assert.Equal("NSError", result.Name);
        Assert.True(result.IsPointer);
        Assert.NotNull(result.PointeeType);
        Assert.Equal("NSError", result.PointeeType!.Name);
        Assert.True(result.PointeeType.IsPointer);
    }

    [Fact]
    public void Parse_NullableDoublePointer_ReturnsPointeeType()
    {
        // NSError * _Nullable * — nullability stripped → NSError * *
        var result = ObjCTypeRefParser.Parse("NSError * _Nullable *");
        Assert.Equal("NSError", result.Name);
        Assert.True(result.IsPointer);
        Assert.NotNull(result.PointeeType);
    }

    [Fact]
    public void Parse_MixedDoublePointer_OuterNullabilityWins()
    {
        // NSError * _Nonnull * _Nullable — rightmost depth-0 annotation (_Nullable) is outer pointer
        var result = ObjCTypeRefParser.Parse("NSError * _Nonnull * _Nullable");
        Assert.Equal("NSError", result.Name);
        Assert.True(result.IsPointer);
        Assert.NotNull(result.PointeeType);
        Assert.Equal(ObjCNullability.Nullable, result.Nullability);
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
    public void Parse_TypeWithNSRefinedForSwift_StripsMacro()
    {
        var result = ObjCTypeRefParser.Parse("NS_REFINED_FOR_SWIFT MOSSchema *");
        Assert.Equal("MOSSchema", result.Name);
        Assert.True(result.IsPointer);
    }

    [Fact]
    public void Parse_NullUnspecified_Stripped()
    {
        var result = ObjCTypeRefParser.Parse("NSString * _Null_unspecified");
        Assert.Equal("NSString", result.Name);
        Assert.True(result.IsPointer);
        Assert.Equal(ObjCNullability.Unspecified, result.Nullability);
    }

    [Fact]
    public void Parse_TypeWithNSSwiftName_StripsMacro()
    {
        var result = ObjCTypeRefParser.Parse("NS_SWIFT_NAME(identifier) NSString *");
        Assert.Equal("NSString", result.Name);
        Assert.True(result.IsPointer);
    }

    [Fact]
    public void Parse_TypeWithAttribute_StripsAttribute()
    {
        var result = ObjCTypeRefParser.Parse("MOSObjectMigrationBlock __attribute__((swift_attr(\"@nonSendable\")))");
        Assert.Equal("MOSObjectMigrationBlock", result.Name);
        Assert.False(result.IsPointer);
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
        var result = ObjCTypeRefParser.Parse("enum LabelPrinterModel");
        Assert.Equal("LabelPrinterModel", result.Name);
        Assert.False(result.IsPointer);
    }

    [Fact]
    public void Parse_StructQualType_StripsStructKeyword()
    {
        var result = ObjCTypeRefParser.Parse("struct CGRect");
        Assert.Equal("CGRect", result.Name);
        Assert.False(result.IsPointer);
    }

    [Fact]
    public void Parse_ConstantArrayType_ReturnsFixedArraySize()
    {
        // Clang qualType for constant array: "uint8_t [4]"
        var result = ObjCTypeRefParser.Parse("uint8_t [4]");
        Assert.Equal("uint8_t", result.Name);
        Assert.Equal(4, result.FixedArraySize);
        Assert.False(result.IsPointer);
    }

    [Fact]
    public void Parse_ConstantArrayType_UnsignedChar()
    {
        // Clang qualType: "unsigned char [3]"
        var result = ObjCTypeRefParser.Parse("unsigned char [3]");
        Assert.Equal("unsigned char", result.Name);
        Assert.Equal(3, result.FixedArraySize);
    }

    [Fact]
    public void Parse_ConstantArrayType_PointerElement()
    {
        // Clang qualType for pointer array: "NSString *[4]"
        var result = ObjCTypeRefParser.Parse("NSString *[4]");
        Assert.Equal("NSString", result.Name);
        Assert.True(result.IsPointer);
        Assert.Equal(4, result.FixedArraySize);
    }

    [Fact]
    public void Parse_NestedBlock_OuterWithInnerVoidBlock()
    {
        // void (^)(UIViewController *, void(^)(void))
        var result = ObjCTypeRefParser.Parse("void (^)(UIViewController *, void(^)(void))");
        Assert.True(result.IsBlock);
        Assert.NotNull(result.BlockReturnType);
        Assert.Equal("void", result.BlockReturnType!.Name);
        Assert.Equal(2, result.BlockParams.Count);
        Assert.Equal("UIViewController", result.BlockParams[0].Name);
        Assert.True(result.BlockParams[0].IsPointer);
        Assert.True(result.BlockParams[1].IsBlock);
    }

    [Fact]
    public void Parse_NestedBlock_InnerBlockWithParams()
    {
        // BOOL (^)(NSString *, void(^)(NSData *, NSError *))
        var result = ObjCTypeRefParser.Parse("BOOL (^)(NSString *, void(^)(NSData *, NSError *))");
        Assert.True(result.IsBlock);
        Assert.Equal("BOOL", result.BlockReturnType!.Name);
        Assert.Equal(2, result.BlockParams.Count);
        Assert.Equal("NSString", result.BlockParams[0].Name);
        Assert.True(result.BlockParams[1].IsBlock);
        Assert.Equal(2, result.BlockParams[1].BlockParams.Count);
        Assert.Equal("NSData", result.BlockParams[1].BlockParams[0].Name);
        Assert.Equal("NSError", result.BlockParams[1].BlockParams[1].Name);
    }

    [Theory]
    [InlineData("NS_AVAILABLE NSString *", "NSString", true)]
    [InlineData("NS_DEPRECATED NSNumber *", "NSNumber", true)]
    [InlineData("API_AVAILABLE NSNumber *", "NSNumber", true)]
    [InlineData("API_DEPRECATED_WITH_REPLACEMENT NSArray *", "NSArray", true)]
    [InlineData("NS_AVAILABLE NSUUID *", "NSUUID", true)]
    [InlineData("API_AVAILABLE(ios(14.0)) NSString *", "NSString", true)]
    [InlineData("NS_DEPRECATED_IOS(8_0, 13_0) NSString *", "NSString", true)]
    public void Parse_AvailabilityMacro_StrippedFromType(string qualType, string expectedName, bool expectedPointer)
    {
        var result = ObjCTypeRefParser.Parse(qualType);
        Assert.Equal(expectedName, result.Name);
        Assert.Equal(expectedPointer, result.IsPointer);
    }

    [Theory]
    [InlineData("const MKMapPoint *", "MKMapPoint", true)]
    [InlineData("const CLLocationCoordinate2D *", "CLLocationCoordinate2D", true)]
    [InlineData("NSString *const", "NSString", true)]
    [InlineData("NSString * const", "NSString", true)]
    [InlineData("const int", "int", false)]
    public void Parse_ConstQualifier_Stripped(string qualType, string expectedName, bool expectedPointer)
    {
        var result = ObjCTypeRefParser.Parse(qualType);
        Assert.Equal(expectedName, result.Name);
        Assert.Equal(expectedPointer, result.IsPointer);
    }

    [Fact]
    public void Parse_AvailabilityMacroOnConstant_StrippedCorrectly()
    {
        // Pattern from system frameworks: "NS_AVAILABLE NSString *const"
        var result = ObjCTypeRefParser.Parse("NS_AVAILABLE NSString *const");
        Assert.Equal("NSString", result.Name);
        Assert.True(result.IsPointer);
    }

    [Fact]
    public void Parse_KindofQualifier_Stripped()
    {
        // __kindof means "this type or any subclass" — strip for binding purposes
        var result = ObjCTypeRefParser.Parse("__kindof CLCondition *");
        Assert.Equal("CLCondition", result.Name);
        Assert.True(result.IsPointer);
    }

    [Fact]
    public void Parse_NullableResult_TreatedAsNullable()
    {
        // _Nullable_result is a newer nullability annotation (Xcode 14+)
        var result = ObjCTypeRefParser.Parse("MKLookAroundScene * _Nullable_result");
        Assert.Equal("MKLookAroundScene", result.Name);
        Assert.True(result.IsPointer);
        Assert.Equal(ObjCNullability.Nullable, result.Nullability);
    }

    [Fact]
    public void Parse_UIAppearanceSelector_Stripped()
    {
        // UI_APPEARANCE_SELECTOR is a UIKit macro in qualType
        var result = ObjCTypeRefParser.Parse("UI_APPEARANCE_SELECTOR UIColor *");
        Assert.Equal("UIColor", result.Name);
        Assert.True(result.IsPointer);
    }

    [Fact]
    public void Parse_TvosProhibited_Stripped()
    {
        var result = ObjCTypeRefParser.Parse("__TVOS_PROHIBITED NSString *");
        Assert.Equal("NSString", result.Name);
        Assert.True(result.IsPointer);
    }

    // --- Fix: OS_ macro prefix stripping ---

    [Theory]
    [InlineData("OS_OBJECT_RETURNS_RETAINED os_log_t", "os_log_t", false)]
    [InlineData("OS_NOTHROW os_log_t", "os_log_t", false)]
    [InlineData("CF_RETURNS_RETAINED CGImageRef", "CGImageRef", false)]
    [InlineData("CF_RETURNS_NOT_RETAINED NSString *", "NSString", true)]
    public void Parse_OSAndCFMacroPrefixes_Stripped(string qualType, string expectedName, bool expectedPointer)
    {
        var result = ObjCTypeRefParser.Parse(qualType);
        Assert.Equal(expectedName, result.Name);
        Assert.Equal(expectedPointer, result.IsPointer);
    }

    [Theory]
    [InlineData("void (*)(int, float)")]
    [InlineData("BOOL (*)(NSError *)")]
    [InlineData("void (* _Nullable)(int)")]
    [InlineData("BOOL (* __nullable)(int, float)")]
    [InlineData("void (*_Nonnull)(void)")]
    public void Parse_FunctionPointer_AllSpellings_Detected(string qualType)
    {
        var result = ObjCTypeRefParser.Parse(qualType);
        Assert.True(result.IsFunctionPointer, $"Expected function pointer for: {qualType}");
        Assert.Equal("FunctionPointer", result.Name);
    }

    // --- 7a: Block nested nullability ---

    [Fact]
    public void Parse_Block_NonnullBlock_NullableParam()
    {
        var result = ObjCTypeRefParser.Parse("void (^ _Nonnull)(NSString * _Nullable)");
        Assert.True(result.IsBlock);
        Assert.Equal(ObjCNullability.Nonnull, result.Nullability);
        Assert.Equal(ObjCNullability.Unspecified, result.BlockReturnType!.Nullability);
        Assert.Single(result.BlockParams);
        Assert.Equal(ObjCNullability.Nullable, result.BlockParams[0].Nullability);
    }

    [Fact]
    public void Parse_Block_NullableBlock_NonnullParam()
    {
        var result = ObjCTypeRefParser.Parse("void (^ _Nullable)(NSString * _Nonnull)");
        Assert.True(result.IsBlock);
        Assert.Equal(ObjCNullability.Nullable, result.Nullability);
        Assert.Single(result.BlockParams);
        Assert.Equal(ObjCNullability.Nonnull, result.BlockParams[0].Nullability);
    }

    [Fact]
    public void Parse_Block_SameAnnotation_BothNullable()
    {
        var result = ObjCTypeRefParser.Parse("void (^ _Nullable)(NSString * _Nullable)");
        Assert.True(result.IsBlock);
        Assert.Equal(ObjCNullability.Nullable, result.Nullability);
        Assert.Single(result.BlockParams);
        Assert.Equal(ObjCNullability.Nullable, result.BlockParams[0].Nullability);
    }

    [Fact]
    public void Parse_Block_NullableReturnType()
    {
        var result = ObjCTypeRefParser.Parse("NSString * _Nullable (^ _Nonnull)(void)");
        Assert.True(result.IsBlock);
        Assert.Equal(ObjCNullability.Nonnull, result.Nullability);
        Assert.Equal(ObjCNullability.Nullable, result.BlockReturnType!.Nullability);
        Assert.Empty(result.BlockParams);
    }

    [Fact]
    public void Parse_Block_MultiParam_MixedNullability()
    {
        var result = ObjCTypeRefParser.Parse("void (^ _Nonnull)(NSString * _Nullable, NSNumber * _Nonnull)");
        Assert.True(result.IsBlock);
        Assert.Equal(ObjCNullability.Nonnull, result.Nullability);
        Assert.Equal(2, result.BlockParams.Count);
        Assert.Equal(ObjCNullability.Nullable, result.BlockParams[0].Nullability);
        Assert.Equal(ObjCNullability.Nonnull, result.BlockParams[1].Nullability);
    }

    [Fact]
    public void Parse_NestedBlock_WithNullability()
    {
        var result = ObjCTypeRefParser.Parse("void (^ _Nullable)(void (^ _Nonnull)(NSString * _Nullable))");
        Assert.True(result.IsBlock);
        Assert.Equal(ObjCNullability.Nullable, result.Nullability);
        Assert.Single(result.BlockParams);
        var inner = result.BlockParams[0];
        Assert.True(inner.IsBlock);
        Assert.Equal(ObjCNullability.Nonnull, inner.Nullability);
        Assert.Single(inner.BlockParams);
        Assert.Equal(ObjCNullability.Nullable, inner.BlockParams[0].Nullability);
    }

    [Fact]
    public void Parse_Block_NoAnnotations_AllUnspecified()
    {
        var result = ObjCTypeRefParser.Parse("void (^)(NSString *)");
        Assert.True(result.IsBlock);
        Assert.Equal(ObjCNullability.Unspecified, result.Nullability);
        Assert.Equal(ObjCNullability.Unspecified, result.BlockReturnType!.Nullability);
        Assert.Single(result.BlockParams);
        Assert.Equal(ObjCNullability.Unspecified, result.BlockParams[0].Nullability);
    }

    // --- 7b: Generic arg nested nullability ---

    [Fact]
    public void Parse_Generic_NonnullOuter_NullableArg()
    {
        var result = ObjCTypeRefParser.Parse("NSArray<NSString * _Nullable> * _Nonnull");
        Assert.Equal("NSArray", result.Name);
        Assert.Equal(ObjCNullability.Nonnull, result.Nullability);
        Assert.Single(result.GenericArgs);
        Assert.Equal(ObjCNullability.Nullable, result.GenericArgs[0].Nullability);
    }

    [Fact]
    public void Parse_Generic_NullableOuter_NonnullArg()
    {
        var result = ObjCTypeRefParser.Parse("NSArray<NSString * _Nonnull> * _Nullable");
        Assert.Equal("NSArray", result.Name);
        Assert.Equal(ObjCNullability.Nullable, result.Nullability);
        Assert.Single(result.GenericArgs);
        Assert.Equal(ObjCNullability.Nonnull, result.GenericArgs[0].Nullability);
    }

    [Fact]
    public void Parse_Generic_SameAnnotation_BothNullable()
    {
        var result = ObjCTypeRefParser.Parse("NSArray<NSString * _Nullable> * _Nullable");
        Assert.Equal("NSArray", result.Name);
        Assert.Equal(ObjCNullability.Nullable, result.Nullability);
        Assert.Single(result.GenericArgs);
        Assert.Equal(ObjCNullability.Nullable, result.GenericArgs[0].Nullability);
    }

    [Fact]
    public void Parse_Generic_NSDictionary_MixedKeyValueNullability()
    {
        var result = ObjCTypeRefParser.Parse("NSDictionary<NSString * _Nonnull, NSNumber * _Nullable> *");
        Assert.Equal("NSDictionary", result.Name);
        Assert.Equal(2, result.GenericArgs.Count);
        Assert.Equal(ObjCNullability.Nonnull, result.GenericArgs[0].Nullability);
        Assert.Equal(ObjCNullability.Nullable, result.GenericArgs[1].Nullability);
    }
}
