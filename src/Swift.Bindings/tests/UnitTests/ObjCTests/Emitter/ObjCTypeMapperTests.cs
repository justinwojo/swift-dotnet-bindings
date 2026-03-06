// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

public class ObjCTypeMapperTests
{
    // Primitive type mappings

    [Theory]
    [InlineData("BOOL", "bool")]
    [InlineData("NSInteger", "nint")]
    [InlineData("NSUInteger", "nuint")]
    [InlineData("CGFloat", "nfloat")]
    [InlineData("void", "void")]
    [InlineData("int", "int")]
    [InlineData("float", "float")]
    [InlineData("double", "double")]
    [InlineData("long", "long")]
    [InlineData("unsigned long", "ulong")]
    [InlineData("short", "short")]
    [InlineData("char", "byte")]
    [InlineData("long long", "long")]
    [InlineData("unsigned long long", "ulong")]
    [InlineData("uint8_t", "byte")]
    [InlineData("int32_t", "int")]
    [InlineData("int64_t", "long")]
    [InlineData("uint32_t", "uint")]
    [InlineData("uint64_t", "ulong")]
    public void MapType_PrimitiveTypes_MapsCorrectly(string objcType, string expected)
    {
        var typeRef = new ObjCTypeRef { Name = objcType };
        Assert.Equal(expected, ObjCTypeMapper.MapType(typeRef));
    }

    // Pointer type mappings

    [Theory]
    [InlineData("NSString", "string")]
    [InlineData("NSArray", "NSArray")]
    [InlineData("NSDictionary", "NSDictionary")]
    [InlineData("NSData", "NSData")]
    [InlineData("NSURL", "NSUrl")]
    [InlineData("NSNumber", "NSNumber")]
    [InlineData("NSError", "NSError")]
    [InlineData("NSSet", "NSSet")]
    [InlineData("NSDate", "NSDate")]
    [InlineData("NSObject", "NSObject")]
    public void MapType_KnownPointerTypes_MapsCorrectly(string objcType, string expected)
    {
        var typeRef = new ObjCTypeRef { Name = objcType, IsPointer = true };
        Assert.Equal(expected, ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_UnknownPointerType_ReturnsNameStripped()
    {
        var typeRef = new ObjCTypeRef { Name = "UIViewController", IsPointer = true };
        Assert.Equal("UIViewController", ObjCTypeMapper.MapType(typeRef));
    }

    // Special types

    [Fact]
    public void MapType_Id_ReturnsNSObject()
    {
        var typeRef = new ObjCTypeRef { Name = "id" };
        Assert.Equal("NSObject", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_SEL_ReturnsSelector()
    {
        var typeRef = new ObjCTypeRef { Name = "SEL" };
        Assert.Equal("Selector", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_Class_ReturnsClass()
    {
        var typeRef = new ObjCTypeRef { Name = "Class" };
        Assert.Equal("Class", ObjCTypeMapper.MapType(typeRef));
    }

    // instancetype

    [Fact]
    public void MapType_Instancetype_WithDeclaringClass_ReturnsClassName()
    {
        var typeRef = new ObjCTypeRef { Name = "instancetype" };
        Assert.Equal("MyClass", ObjCTypeMapper.MapType(typeRef, "MyClass"));
    }

    [Fact]
    public void MapType_Instancetype_WithoutDeclaringClass_ReturnsNSObject()
    {
        var typeRef = new ObjCTypeRef { Name = "instancetype" };
        Assert.Equal("NSObject", ObjCTypeMapper.MapType(typeRef));
    }

    // Protocol-qualified id

    [Fact]
    public void MapType_ProtocolQualifiedId_ReturnsIProtocol()
    {
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualification = "UITableViewDelegate" };
        Assert.Equal("IUITableViewDelegate", ObjCTypeMapper.MapType(typeRef));
    }

    // Block types

    [Fact]
    public void MapType_BlockVoidNoParams_ReturnsAction()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "void" },
        };
        Assert.Equal("Action", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_BlockVoidWithParams_ReturnsActionOfT()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "void" },
            BlockParams = [new ObjCTypeRef { Name = "BOOL" }, new ObjCTypeRef { Name = "NSInteger" }],
        };
        Assert.Equal("Action<bool, nint>", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_BlockNonVoidReturn_ReturnsFunc()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "BOOL" },
            BlockParams = [new ObjCTypeRef { Name = "NSString", IsPointer = true }],
        };
        Assert.Equal("Func<string, bool>", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_BlockNonVoidNoParams_ReturnsFuncOfR()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "int" },
        };
        Assert.Equal("Func<int>", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_BlockOver16Params_ReturnsNSObject()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "void" },
            BlockParams = Enumerable.Range(0, 17).Select(_ => new ObjCTypeRef { Name = "int" }).ToList(),
        };
        Assert.Equal("NSObject", ObjCTypeMapper.MapType(typeRef));
    }

    // Passthrough types

    [Theory]
    [InlineData("CGRect")]
    [InlineData("CGPoint")]
    [InlineData("CGSize")]
    public void MapType_PassthroughTypes_ReturnedAsIs(string typeName)
    {
        var typeRef = new ObjCTypeRef { Name = typeName };
        Assert.Equal(typeName, ObjCTypeMapper.MapType(typeRef));
    }

    // IsNullableAttribute

    [Fact]
    public void IsNullableAttribute_Nullable_ReturnsTrue()
    {
        var typeRef = new ObjCTypeRef { Name = "NSString", Nullability = ObjCNullability.Nullable };
        Assert.True(ObjCTypeMapper.IsNullableAttribute(typeRef));
    }

    [Fact]
    public void IsNullableAttribute_Nonnull_ReturnsFalse()
    {
        var typeRef = new ObjCTypeRef { Name = "NSString", Nullability = ObjCNullability.Nonnull };
        Assert.False(ObjCTypeMapper.IsNullableAttribute(typeRef));
    }

    [Fact]
    public void IsNullableAttribute_Unspecified_ReturnsFalse()
    {
        var typeRef = new ObjCTypeRef { Name = "NSString", Nullability = ObjCNullability.Unspecified };
        Assert.False(ObjCTypeMapper.IsNullableAttribute(typeRef));
    }

    // IsNSErrorOutParameter

    [Fact]
    public void IsNSErrorOutParameter_DoublePointerNSError_ReturnsTrue()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSError",
            IsPointer = true,
            PointeeType = new ObjCTypeRef { Name = "NSError", IsPointer = true },
        };
        Assert.True(ObjCTypeMapper.IsNSErrorOutParameter(typeRef));
    }

    [Fact]
    public void IsNSErrorOutParameter_SinglePointerNSError_ReturnsFalse()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSError",
            IsPointer = true,
            PointeeType = new ObjCTypeRef { Name = "NSError" },
        };
        Assert.False(ObjCTypeMapper.IsNSErrorOutParameter(typeRef));
    }

    [Fact]
    public void IsNSErrorOutParameter_NotNSError_ReturnsFalse()
    {
        var typeRef = new ObjCTypeRef { Name = "NSString", IsPointer = true };
        Assert.False(ObjCTypeMapper.IsNSErrorOutParameter(typeRef));
    }

    [Fact]
    public void IsNSErrorOutParameter_NoPointee_ReturnsFalse()
    {
        var typeRef = new ObjCTypeRef { Name = "NSError", IsPointer = true };
        Assert.False(ObjCTypeMapper.IsNSErrorOutParameter(typeRef));
    }

    [Fact]
    public void MapType_UnsignedInt_MapsToUint()
    {
        var typeRef = new ObjCTypeRef { Name = "unsigned int" };
        Assert.Equal("uint", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_UnsignedShort_MapsToUshort()
    {
        var typeRef = new ObjCTypeRef { Name = "unsigned short" };
        Assert.Equal("ushort", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_UnsignedChar_MapsToByte()
    {
        var typeRef = new ObjCTypeRef { Name = "unsigned char" };
        Assert.Equal("byte", ObjCTypeMapper.MapType(typeRef));
    }
}
