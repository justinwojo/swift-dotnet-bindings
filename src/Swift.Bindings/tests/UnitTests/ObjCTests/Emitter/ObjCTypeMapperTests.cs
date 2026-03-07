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
    [InlineData("NSTimeInterval", "double")]
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
    [InlineData("UInt8", "byte")]
    [InlineData("va_list", "IntPtr")]
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
    [InlineData("CGImageRef", "CGImage")]
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

    [Fact]
    public void MapType_MultiProtocolId_UsesFirstNonNSObject()
    {
        // id<FIRLocalCacheSettings,NSObject> → IFIRLocalCacheSettings (not IFIRLocalCacheSettings,NSObject)
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualification = "FIRLocalCacheSettings,NSObject" };
        Assert.Equal("IFIRLocalCacheSettings", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_MultiProtocolId_WithSpaces()
    {
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualification = "Proto1, Proto2, NSObject" };
        Assert.Equal("IProto1", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_MultiProtocolId_AllNSObject_ReturnsNSObject()
    {
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualification = "NSObject" };
        Assert.Equal("NSObject", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_MultiProtocolId_NSFastEnumerationFirst_SkipsToBindable()
    {
        // id<NSFastEnumeration, FIRFoo> — NSFastEnumeration has no binding, should use FIRFoo
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualification = "NSFastEnumeration, FIRFoo" };
        Assert.Equal("IFIRFoo", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_MultiProtocolId_NSObjectAndNSFastEnumerationBeforeBindable()
    {
        // id<NSObject, NSFastEnumeration, FIRFoo> — both filtered, should use FIRFoo
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualification = "NSObject, NSFastEnumeration, FIRFoo" };
        Assert.Equal("IFIRFoo", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_MultiProtocolId_OnlyUnbindable_ReturnsNSObject()
    {
        // id<NSObject, NSFastEnumeration> — nothing bindable left
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualification = "NSObject, NSFastEnumeration" };
        Assert.Equal("NSObject", ObjCTypeMapper.MapType(typeRef));
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

    // ObjC lightweight generic type parameters — AST-driven only (no hardcoded fallback)

    [Theory]
    [InlineData("ObjectType")]
    [InlineData("T")]
    [InlineData("KeyType")]
    [InlineData("ValueType")]
    [InlineData("ElementType")]
    [InlineData("RLMObjectType")]
    [InlineData("RLMKeyType")]
    public void MapType_GenericTypeParam_WithAstSet_ReturnsNSObject(string typeName)
    {
        var typeRef = new ObjCTypeRef { Name = typeName };
        var genericParams = new HashSet<string> { typeName };
        Assert.Equal("NSObject", ObjCTypeMapper.MapType(typeRef, genericTypeParams: genericParams));
    }

    [Theory]
    [InlineData("ObjectType")]
    [InlineData("T")]
    [InlineData("KeyType")]
    [InlineData("RLMObjectType")]
    public void MapType_GenericTypeParam_WithoutAstSet_FallsThrough(string typeName)
    {
        // Without the genericTypeParams set, generic param names are NOT auto-recognized.
        // This prevents cross-type collisions where a generic param name in one class
        // matches a real type name used elsewhere.
        var typeRef = new ObjCTypeRef { Name = typeName };
        Assert.Equal(typeName, ObjCTypeMapper.MapType(typeRef));
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

    // CoreFoundation Ref types (non-pointer typedefs)

    [Theory]
    [InlineData("CGImageRef", "CGImage")]
    [InlineData("CGColorRef", "CGColor")]
    [InlineData("CGPathRef", "CGPath")]
    [InlineData("CGContextRef", "CGContext")]
    [InlineData("dispatch_queue_t", "DispatchQueue")]
    [InlineData("dispatch_data_t", "DispatchData")]
    public void MapType_CoreFoundationRef_MapsCorrectly(string objcType, string expected)
    {
        var typeRef = new ObjCTypeRef { Name = objcType };
        Assert.Equal(expected, ObjCTypeMapper.MapType(typeRef));
    }

    // NSFastEnumeration protocol-qualified id

    [Fact]
    public void MapType_NSFastEnumeration_ProtocolQualified_ReturnsNSObject()
    {
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualification = "NSFastEnumeration" };
        Assert.Equal("NSObject", ObjCTypeMapper.MapType(typeRef));
    }

    // NSURLSession pointer mapping

    [Fact]
    public void MapType_NSURLSession_Pointer_ReturnsNSUrlSession()
    {
        var typeRef = new ObjCTypeRef { Name = "NSURLSession", IsPointer = true };
        Assert.Equal("NSUrlSession", ObjCTypeMapper.MapType(typeRef));
    }

    // Nested block mapping

    [Fact]
    public void MapType_NestedBlock_MapsToActionOfAction()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "Block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "void" },
            BlockParams =
            [
                new ObjCTypeRef { Name = "UIViewController", IsPointer = true },
                new ObjCTypeRef
                {
                    Name = "Block",
                    IsBlock = true,
                    BlockReturnType = new ObjCTypeRef { Name = "void" },
                }
            ],
        };
        Assert.Equal("Action<UIViewController, Action>", ObjCTypeMapper.MapType(typeRef));
    }

    // Typedef alias resolution

    [Fact]
    public void MapType_TypedefAlias_ResolvesToUnderlying()
    {
        var typeRef = new ObjCTypeRef { Name = "BRLMSerialNumber" };
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["BRLMSerialNumber"] = new ObjCTypeRef { Name = "NSString", IsPointer = true }
        };
        Assert.Equal("string", ObjCTypeMapper.MapType(typeRef, typedefMap: typedefMap));
    }

    [Fact]
    public void MapType_TypedefAlias_MultiHopChain()
    {
        // BuildResolvedTypedefMap pre-resolves chains, so the map already contains final types
        var typeRef = new ObjCTypeRef { Name = "MyAlias" };
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["MyAlias"] = new ObjCTypeRef { Name = "NSString", IsPointer = true }
        };
        Assert.Equal("string", ObjCTypeMapper.MapType(typeRef, typedefMap: typedefMap));
    }

    [Fact]
    public void MapType_TypedefAlias_NotInMap_PassesThrough()
    {
        var typeRef = new ObjCTypeRef { Name = "UnknownType" };
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["SomeOtherType"] = new ObjCTypeRef { Name = "NSString", IsPointer = true }
        };
        Assert.Equal("UnknownType", ObjCTypeMapper.MapType(typeRef, typedefMap: typedefMap));
    }

    [Fact]
    public void BuildResolvedTypedefMap_ResolvesChain()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Typedefs =
            [
                new ObjCTypedefDecl { Name = "AliasA", UnderlyingType = new ObjCTypeRef { Name = "AliasB" } },
                new ObjCTypedefDecl { Name = "AliasB", UnderlyingType = new ObjCTypeRef { Name = "NSString", IsPointer = true } },
            ]
        };
        var map = ObjCTypeMapper.BuildResolvedTypedefMap(module);
        Assert.Equal("NSString", map["AliasA"].Name);
        Assert.True(map["AliasA"].IsPointer);
    }

    [Fact]
    public void BuildResolvedTypedefMap_ExcludesBlockTypedefs()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "MyBlock",
                    UnderlyingType = new ObjCTypeRef
                    {
                        Name = "Block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" }
                    }
                },
            ]
        };
        var map = ObjCTypeMapper.BuildResolvedTypedefMap(module);
        Assert.Empty(map);
    }

    [Fact]
    public void BuildResolvedTypedefMap_ExcludesStructTypedefs()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Structs = [new ObjCStructDecl { Name = "MyStruct" }],
            Typedefs =
            [
                new ObjCTypedefDecl { Name = "MyStruct", UnderlyingType = new ObjCTypeRef { Name = "MyStruct" } },
            ]
        };
        var map = ObjCTypeMapper.BuildResolvedTypedefMap(module);
        Assert.Empty(map);
    }

    // --- Typedef pointer preservation ---

    [Fact]
    public void MapType_TypedefAlias_PointerPreserved()
    {
        // typedef NSString BRAlias; usage: BRAlias * → should resolve to string (NSString *)
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["BRAlias"] = new ObjCTypeRef { Name = "NSString", IsPointer = false }
        };
        var typeRef = new ObjCTypeRef { Name = "BRAlias", IsPointer = true };
        Assert.Equal("string", ObjCTypeMapper.MapType(typeRef, typedefMap: typedefMap));
    }

    [Fact]
    public void MapType_TypedefAlias_NonPointer_ResolvesDirectly()
    {
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["MyAlias"] = new ObjCTypeRef { Name = "NSInteger" }
        };
        var typeRef = new ObjCTypeRef { Name = "MyAlias" };
        Assert.Equal("nint", ObjCTypeMapper.MapType(typeRef, typedefMap: typedefMap));
    }

    // --- BOOL pointer mapping ---

    [Fact]
    public void MapType_BOOLPointer_ReturnsBool()
    {
        var typeRef = new ObjCTypeRef { Name = "BOOL", IsPointer = true };
        Assert.Equal("bool", ObjCTypeMapper.MapType(typeRef));
    }

    // --- Unknown pointer types fall through to typedef resolution ---

    [Fact]
    public void MapType_UnknownPointerType_FallsThroughToTypedefResolution()
    {
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["CustomType"] = new ObjCTypeRef { Name = "NSString", IsPointer = false }
        };
        // CustomType * → typedef resolves to NSString, pointer preserved → string
        var typeRef = new ObjCTypeRef { Name = "CustomType", IsPointer = true };
        Assert.Equal("string", ObjCTypeMapper.MapType(typeRef, typedefMap: typedefMap));
    }

    [Fact]
    public void MapType_UnknownPointerType_NoTypedefMap_PassesThrough()
    {
        var typeRef = new ObjCTypeRef { Name = "UIView", IsPointer = true };
        Assert.Equal("UIView", ObjCTypeMapper.MapType(typeRef));
    }

    // --- Block typedef map ---

    [Fact]
    public void MapType_BlockTypedefName_ResolvesToActionFunc()
    {
        var blockTypedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["MyCallback"] = new ObjCTypeRef
            {
                Name = "block",
                IsBlock = true,
                BlockReturnType = new ObjCTypeRef { Name = "void" },
                BlockParams = [new ObjCTypeRef { Name = "NSString", IsPointer = true }]
            }
        };
        var typeRef = new ObjCTypeRef { Name = "MyCallback" };
        Assert.Equal("Action<string>", ObjCTypeMapper.MapType(typeRef, blockTypedefMap: blockTypedefMap));
    }

    [Fact]
    public void MapType_FixedArraySize_ReturnsMappedElementWithSize()
    {
        // uint8_t [4] → FixedArraySize=4, Name="uint8_t" → "byte[4]"
        var typeRef = new ObjCTypeRef { Name = "uint8_t", FixedArraySize = 4 };
        Assert.Equal("byte[4]", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_FixedArraySize_UnsignedChar()
    {
        var typeRef = new ObjCTypeRef { Name = "unsigned char", FixedArraySize = 3 };
        Assert.Equal("byte[3]", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_FixedArraySize_PointerElement()
    {
        // NSString *[4] → Name="NSString", IsPointer=true, FixedArraySize=4 → "string[4]"
        var typeRef = new ObjCTypeRef { Name = "NSString", IsPointer = true, FixedArraySize = 4 };
        Assert.Equal("string[4]", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void BuildBlockTypedefMap_ReturnsOnlyBlockTypedefs()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "MyBlock",
                    UnderlyingType = new ObjCTypeRef { Name = "block", IsBlock = true, BlockReturnType = new ObjCTypeRef { Name = "void" } }
                },
                new ObjCTypedefDecl
                {
                    Name = "MyAlias",
                    UnderlyingType = new ObjCTypeRef { Name = "NSString", IsPointer = true }
                }
            ]
        };
        var map = ObjCTypeMapper.BuildBlockTypedefMap(module);
        Assert.Single(map);
        Assert.True(map.ContainsKey("MyBlock"));
        Assert.False(map.ContainsKey("MyAlias"));
    }

    [Theory]
    [InlineData("uint16_t", false, "ushort")]
    [InlineData("int16_t", false, "short")]
    [InlineData("CFAbsoluteTime", false, "double")]
    [InlineData("Float64", false, "double")]
    [InlineData("CLLocationDegrees", false, "double")]
    [InlineData("size_t", false, "nuint")]
    [InlineData("NSUUID", true, "NSUuid")]
    [InlineData("NSURLRequest", true, "NSUrlRequest")]
    public void MapType_NewSystemTypes(string name, bool isPointer, string expected)
    {
        var typeRef = new ObjCTypeRef { Name = name, IsPointer = isPointer };
        Assert.Equal(expected, ObjCTypeMapper.MapType(typeRef));
    }

    [Theory]
    [InlineData("CFTypeRef", "IntPtr")]
    [InlineData("CFArrayRef", "IntPtr")]
    [InlineData("CFStringRef", "IntPtr")]
    [InlineData("CGColorSpaceRef", "CGColorSpace")]
    [InlineData("CVPixelBufferRef", "IntPtr")]
    [InlineData("dispatch_block_t", "Action")]
    public void MapType_CoreFoundationRefTypes(string name, string expected)
    {
        var typeRef = new ObjCTypeRef { Name = name };
        Assert.Equal(expected, ObjCTypeMapper.MapType(typeRef));
    }

    // --- Fix: void* → IntPtr (not "void") ---

    [Fact]
    public void MapType_VoidPointer_ReturnsIntPtr()
    {
        var typeRef = new ObjCTypeRef { Name = "void", IsPointer = true };
        Assert.Equal("IntPtr", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_VoidNonPointer_ReturnsVoid()
    {
        var typeRef = new ObjCTypeRef { Name = "void" };
        Assert.Equal("void", ObjCTypeMapper.MapType(typeRef));
    }

    // --- Fix: NSURL* → NSUrl* naming convention ---

    [Theory]
    [InlineData("NSURLSessionTask", "NSUrlSessionTask")]
    [InlineData("NSURLSessionConfiguration", "NSUrlSessionConfiguration")]
    [InlineData("NSURLSessionTaskMetrics", "NSUrlSessionTaskMetrics")]
    [InlineData("NSURLCredential", "NSUrlCredential")]
    [InlineData("NSURLSessionDataTask", "NSUrlSessionDataTask")]
    [InlineData("NSURLCache", "NSUrlCache")]
    [InlineData("NSHTTPURLResponse", "NSHttpUrlResponse")]
    [InlineData("NSHTTPCookie", "NSHttpCookie")]
    public void MapType_NSURLPointerTypes_MapsToNetConvention(string objcType, string expected)
    {
        var typeRef = new ObjCTypeRef { Name = objcType, IsPointer = true };
        Assert.Equal(expected, ObjCTypeMapper.MapType(typeRef));
    }

    // --- Fix: NSURL prefix fallback for unmapped variants ---

    [Theory]
    [InlineData("NSURLSessionTaskDelegate", "NSUrlSessionTaskDelegate")]
    [InlineData("NSURLSessionDataDelegate", "NSUrlSessionDataDelegate")]
    [InlineData("NSURLSessionWebSocketMessage", "NSUrlSessionWebSocketMessage")]
    public void MapType_NSURLPrefixFallback_MapsCorrectly(string objcType, string expected)
    {
        // These types aren't in PointerTypeMappings but hit the prefix-based fallback
        var typeRef = new ObjCTypeRef { Name = objcType };
        Assert.Equal(expected, ObjCTypeMapper.MapType(typeRef));
    }

    // --- Fix: Unmapped C/OS types ---

    [Theory]
    [InlineData("dispatch_queue_attr_t", "IntPtr")]
    [InlineData("os_log_t", "IntPtr")]
    [InlineData("os_log_type_t", "byte")]
    [InlineData("dispatch_semaphore_t", "IntPtr")]
    [InlineData("SecKeyRef", "IntPtr")]
    [InlineData("SecTrustRef", "IntPtr")]
    public void MapType_SystemCTypes_MapsCorrectly(string name, string expected)
    {
        var typeRef = new ObjCTypeRef { Name = name };
        Assert.Equal(expected, ObjCTypeMapper.MapType(typeRef));
    }

    // Protocol name mapping

    [Theory]
    [InlineData("NSURLSessionTaskDelegate", "NSUrlSessionTaskDelegate")]
    [InlineData("NSURLSessionDataDelegate", "NSUrlSessionDataDelegate")]
    [InlineData("NSURLSessionDelegate", "NSUrlSessionDelegate")]
    [InlineData("NSHTTPCookieStorageDelegate", "NSHttpCookieStorageDelegate")]
    [InlineData("NSCoding", "NSCoding")]
    [InlineData("UITableViewDelegate", "UITableViewDelegate")]
    public void MapProtocolName_AppliesNamingConvention(string input, string expected)
    {
        Assert.Equal(expected, ObjCTypeMapper.MapProtocolName(input));
    }

    [Fact]
    public void MapType_IdWithNSURLProtocol_MapsProtocolName()
    {
        var typeRef = new ObjCTypeRef { Name = "id", IsPointer = true, ProtocolQualification = "NSURLSessionDelegate" };
        Assert.Equal("INSUrlSessionDelegate", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_TypedefAliasToBlockTypedef_ResolvesToAction()
    {
        // Simulates: typedef void(^OriginalBlock)(NSString *);
        //            typedef OriginalBlock AliasBlock;
        // Parameter qualType: "AliasBlock" → should resolve through typedefMap to OriginalBlock,
        // then through blockTypedefMap to Action<string>.
        var blockTypeRef = new ObjCTypeRef
        {
            Name = "",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "void" },
            BlockParams = [new ObjCTypeRef { Name = "NSString", IsPointer = true }]
        };

        // typedefMap: AliasBlock → { Name = "OriginalBlock", IsBlock = false }
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["AliasBlock"] = new ObjCTypeRef { Name = "OriginalBlock" }
        };

        // blockTypedefMap: OriginalBlock → block signature
        var blockTypedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["OriginalBlock"] = blockTypeRef
        };

        var paramType = new ObjCTypeRef { Name = "AliasBlock" };
        var result = ObjCTypeMapper.MapType(paramType, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap);
        Assert.Equal("Action<string>", result);
    }
}
