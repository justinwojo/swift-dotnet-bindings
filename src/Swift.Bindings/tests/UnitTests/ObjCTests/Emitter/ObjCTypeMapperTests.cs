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
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["UITableViewDelegate"] };
        Assert.Equal("IUITableViewDelegate", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_MultiProtocolId_UsesFirstNonNSObject()
    {
        // id<CloudPlatformSdkLocalCacheSettings,NSObject> → ICloudPlatformSdkLocalCacheSettings (not ICloudPlatformSdkLocalCacheSettings,NSObject)
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["CloudPlatformSdkLocalCacheSettings", "NSObject"] };
        Assert.Equal("ICloudPlatformSdkLocalCacheSettings", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_MultiProtocolId_WithSpaces()
    {
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["Proto1", "Proto2", "NSObject"] };
        Assert.Equal("IProto1", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_MultiProtocolId_AllNSObject_ReturnsNSObject()
    {
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["NSObject"] };
        Assert.Equal("NSObject", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_MultiProtocolId_NSFastEnumerationFirst_SkipsToBindable()
    {
        // id<NSFastEnumeration, CloudPlatformSdkFoo> — NSFastEnumeration has no binding, should use CloudPlatformSdkFoo
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["NSFastEnumeration", "CloudPlatformSdkFoo"] };
        Assert.Equal("ICloudPlatformSdkFoo", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_MultiProtocolId_NSObjectAndNSFastEnumerationBeforeBindable()
    {
        // id<NSObject, NSFastEnumeration, CloudPlatformSdkFoo> — both filtered, should use CloudPlatformSdkFoo
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["NSObject", "NSFastEnumeration", "CloudPlatformSdkFoo"] };
        Assert.Equal("ICloudPlatformSdkFoo", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_MultiProtocolId_OnlyUnbindable_ReturnsNSObject()
    {
        // id<NSObject, NSFastEnumeration> — nothing bindable left
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["NSObject", "NSFastEnumeration"] };
        Assert.Equal("NSObject", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_ConcreteTypeWithProtocolQualification_MapsToConcreteType()
    {
        // NSObject<NSCopying> * → NSObject (protocol qualification is metadata, doesn't change C# type)
        var typeRef = new ObjCTypeRef { Name = "NSObject", IsPointer = true, ProtocolQualifications = ["NSCopying"] };
        Assert.Equal("NSObject", ObjCTypeMapper.MapType(typeRef));
    }

    // A protocol-typed MEMBER reference (parameter/return/property) binds to the protocol
    // INTERFACE `IFoo` — whether the protocol is declared in THIS binding or comes from the SDK.
    // Binding a member to the bare name makes bgen pick the generated Model class, so a conforming
    // subclass throws InvalidCastException at runtime. (The own-vs-SDK distinction lives only in the
    // declaration — always bare — and conformance lists; not in member types.)
    [Fact]
    public void MapType_ProtocolQualifiedId_LocalProtocol_ReturnsInterface()
    {
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["MLNFeature"] };
        var local = new HashSet<string>(StringComparer.Ordinal) { "MLNFeature" };
        Assert.Equal("IMLNFeature", ObjCTypeMapper.MapType(typeRef, localProtocolNames: local));
    }

    [Fact]
    public void MapType_ProtocolQualifiedId_SdkProtocol_StaysIPrefixed()
    {
        // NSCopying is not declared in this binding, so it keeps its `I` prefix even when a
        // set of local protocols is supplied.
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["NSCopying"] };
        var local = new HashSet<string>(StringComparer.Ordinal) { "MLNFeature" };
        Assert.Equal("INSCopying", ObjCTypeMapper.MapType(typeRef, localProtocolNames: local));
    }

    [Fact]
    public void MapType_DirectLocalProtocolPointer_ReturnsInterface()
    {
        // A direct protocol-pointer member (`MLNAnnotation *`, parsed without an id<…>
        // qualification) also binds to the protocol interface `IMLNAnnotation`, not the bare
        // Model class — otherwise it falls through to the acronym fallback and bgen mis-binds it.
        var typeRef = new ObjCTypeRef { Name = "MLNAnnotation", IsPointer = true };
        var local = new HashSet<string>(StringComparer.Ordinal) { "MLNAnnotation" };
        Assert.Equal("IMLNAnnotation", ObjCTypeMapper.MapType(typeRef, localProtocolNames: local));
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
    public void MapType_BlockReturningProtocolId_WidensReturnToNSObject()
    {
        // A block that RETURNS `id<Proto>` (e.g. AdMob's mediation LoadCompletionHandler) must
        // bind the return slot to NSObject, NOT the protocol interface IProto. bgen marshals a
        // block's return through Runtime.RetainAndAutoreleaseNSObject(NSObject?), which cannot take
        // an INativeObject interface — emitting IProto there fails to compile (CS1503) in
        // Trampolines.g.cs. Parameters keep IProto (bgen reads them via GetINativeObject<IProto>).
        var typeRef = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "id", IsPointer = true, ProtocolQualifications = ["GADMediationBannerAdEventDelegate"] },
            BlockParams = [new ObjCTypeRef { Name = "id", IsPointer = true, ProtocolQualifications = ["GADMediationBannerAd"] }, new ObjCTypeRef { Name = "NSError", IsPointer = true }],
        };
        // Return -> NSObject; parameter protocol id<GADMediationBannerAd> keeps its IProto interface.
        Assert.Equal("Func<IGADMediationBannerAd, NSError, NSObject>", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_BlockReturningBareProtocolName_WidensReturnToNSObject()
    {
        // Same rule via the bare own-protocol-name form (MapType arm 10b): a block returning
        // `SomeProto *` where SomeProto is a local protocol still widens the return to NSObject.
        var local = new HashSet<string> { "MyProto" };
        var typeRef = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "MyProto", IsPointer = true },
            BlockParams = [new ObjCTypeRef { Name = "NSString", IsPointer = true }],
        };
        Assert.Equal("Func<string, NSObject>", ObjCTypeMapper.MapType(typeRef, localProtocolNames: local));
        // A block PARAMETER typed by the same bare protocol name keeps IMyProto (only return widens).
        var paramTypeRef = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "void" },
            BlockParams = [new ObjCTypeRef { Name = "MyProto", IsPointer = true }],
        };
        Assert.Equal("Action<IMyProto>", ObjCTypeMapper.MapType(paramTypeRef, localProtocolNames: local));
    }

    [Fact]
    public void MapType_BlockReturningConcreteClass_Unaffected()
    {
        // Only protocol-typed returns widen. A block returning a concrete class keeps that class.
        var typeRef = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "NSString", IsPointer = true },
            BlockParams = [new ObjCTypeRef { Name = "NSInteger" }],
        };
        Assert.Equal("Func<nint, string>", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_BlockReturningTypedefIdProtocolAlias_WidensReturnToNSObject()
    {
        // `typedef id<Proto> Alias;` used as a block return. The alias name is neither `id` nor a
        // local protocol, so it must resolve one typedef hop and re-check the id<Proto> form —
        // then widen to NSObject exactly as a direct id<Proto> return does.
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["MyDelegateAlias"] = new ObjCTypeRef { Name = "id", IsPointer = true, ProtocolQualifications = ["MyProto"] },
        };
        var typeRef = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "MyDelegateAlias", IsPointer = true },
            BlockParams = [new ObjCTypeRef { Name = "NSInteger" }],
        };
        Assert.Equal("Func<nint, NSObject>", ObjCTypeMapper.MapType(typeRef, typedefMap: typedefMap));
    }

    [Fact]
    public void MapType_BlockReturningTypedefBareProtocolAlias_WidensReturnToNSObject()
    {
        // `typedef Proto Alias;` (an alias of a BARE own-protocol name) used as a block return. The
        // hop resolves to a bare local protocol, which MapType maps to IProto (arm 10b) — so the
        // return must widen to NSObject. Re-checking only the id<Proto> form after the hop would
        // leak IProto into the block-return slot (CS1503 in the generated trampoline).
        var local = new HashSet<string> { "MyProto" };
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["MyProtoAlias"] = new ObjCTypeRef { Name = "MyProto", IsPointer = true },
        };
        var typeRef = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "MyProtoAlias", IsPointer = true },
            BlockParams = [new ObjCTypeRef { Name = "NSString", IsPointer = true }],
        };
        Assert.Equal("Func<string, NSObject>", ObjCTypeMapper.MapType(typeRef, typedefMap: typedefMap, localProtocolNames: local));
    }

    [Fact]
    public void MapType_BlockReturningTypedefConcreteAlias_NotWidened()
    {
        // The typedef hop must NOT over-widen: an alias resolving to a concrete class is not a
        // protocol interface, so the return keeps the concrete mapping (here `string`).
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["MyStringAlias"] = new ObjCTypeRef { Name = "NSString", IsPointer = true },
        };
        var typeRef = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "MyStringAlias", IsPointer = true },
            BlockParams = [new ObjCTypeRef { Name = "NSInteger" }],
        };
        Assert.Equal("Func<nint, string>", ObjCTypeMapper.MapType(typeRef, typedefMap: typedefMap));
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
    [InlineData("MOSObjectType")]
    [InlineData("MOSKeyType")]
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
    [InlineData("MOSObjectType")]
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
    [InlineData("CMSampleBufferRef", "CMSampleBuffer")]
    public void MapType_CoreFoundationRef_MapsCorrectly(string objcType, string expected)
    {
        var typeRef = new ObjCTypeRef { Name = objcType };
        Assert.Equal(expected, ObjCTypeMapper.MapType(typeRef));
    }

    // NSFastEnumeration protocol-qualified id

    [Fact]
    public void MapType_NSFastEnumeration_ProtocolQualified_ReturnsNSObject()
    {
        var typeRef = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["NSFastEnumeration"] };
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
        var typeRef = new ObjCTypeRef { Name = "LabelPrinterSerialNumber" };
        var typedefMap = new Dictionary<string, ObjCTypeRef>
        {
            ["LabelPrinterSerialNumber"] = new ObjCTypeRef { Name = "NSString", IsPointer = true }
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
    // NSXPC types are declared in Foundation but Microsoft.iOS projects them under the
    // .NET acronym convention (NSXpc*). The Matter framework's MTRDeviceController+XPC
    // and MTRXPCDeviceControllerParameters reference these directly.
    [InlineData("NSXPCConnection", "NSXpcConnection")]
    [InlineData("NSXPCInterface", "NSXpcInterface")]
    [InlineData("NSXPCListener", "NSXpcListener")]
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
    // Acronym conventions on additional Apple-projected acronyms:
    // NSXPC* exists in Microsoft.iOS as NSXpc*; NSJSON* as NSJson*; NSHTML* as NSHtml*;
    // NSHTTPS overlap with NSHTTP is resolved by longer-first ordering in AcronymConventions.
    [InlineData("NSXPCListenerDelegate", "NSXpcListenerDelegate")]
    [InlineData("NSXPCProxyCreating", "NSXpcProxyCreating")]
    [InlineData("NSJSONSerialization", "NSJsonSerialization")]
    [InlineData("NSHTMLReader", "NSHtmlReader")]
    [InlineData("NSHTTPSConnection", "NSHttpsConnection")]
    public void MapProtocolName_AppliesNamingConvention(string input, string expected)
    {
        Assert.Equal(expected, ObjCTypeMapper.MapProtocolName(input));
    }

    [Fact]
    public void MapType_IdWithNSURLProtocol_MapsProtocolName()
    {
        var typeRef = new ObjCTypeRef { Name = "id", IsPointer = true, ProtocolQualifications = ["NSURLSessionDelegate"] };
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

    // ──────────────────────────────────────────────
    // Signed char / int8_t primitive mapping
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("signed char", "sbyte")]
    [InlineData("int8_t", "sbyte")]
    public void MapType_SignedCharAndInt8t_MapToSbyte(string cType, string expected)
    {
        var typeRef = new ObjCTypeRef { Name = cType };
        var result = ObjCTypeMapper.MapType(typeRef);
        Assert.Equal(expected, result);
    }

    // ──────────────────────────────────────────────
    // IsTypeResolvable — ObjC vs C type heuristic
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("CGBitmapInfo", true)]     // Apple framework type (CoreGraphics)
    [InlineData("UIColor", true)]          // Apple framework type (UIKit)
    [InlineData("NSCoder", true)]          // Apple framework type (Foundation)
    [InlineData("SDImagePixelFormat", true)] // Module-defined ObjC struct (CamelCase)
    [InlineData("int", true)]              // Known primitive (in knownTypes)
    [InlineData("Action<string>", true)]   // Delegate pattern
    [InlineData("byte[]", true)]           // Array pattern
    [InlineData("pb_wire_type_t", false)]  // C-internal type (snake_case)
    [InlineData("pb_type_t", false)]       // C-internal type (snake_case)
    [InlineData("some_struct", false)]     // C-internal type (snake_case)
    public void IsTypeResolvable_DistinguishesObjCFromCTypes(string mappedType, bool expected)
    {
        var knownTypes = ObjCTypeMapper.BuildKnownMappedTypes();
        Assert.Equal(expected, ObjCTypeMapper.IsTypeResolvable(mappedType, knownTypes));
    }

    // ──────────────────────────────────────────────
    // IsApiDefinitionTypeResolvable — source-aware + acronym rename
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("NSHttpUrlResponse", true)]       // Mapped from NSHTTPURLResponse (in SDK set)
    [InlineData("NSUrlSession", true)]            // Mapped from NSURLSession (in SDK set)
    [InlineData("INSUrlSessionDelegate", true)]   // I-prefix protocol: strip I → NSUrlSessionDelegate → reverse → NSURLSessionDelegate
    [InlineData("UIColor", true)]                 // Direct match (in SDK set as-is)
    [InlineData("ThirdPartyType", false)]         // Not in SDK set or known types
    [InlineData("pb_wire_type_t", false)]         // C-internal type
    [InlineData("NSXpcConnection", true)]         // Reverse acronym: NSXpcConnection → NSXPCConnection (in SDK set)
    [InlineData("NSXpcInterface", true)]          // Reverse acronym: NSXpcInterface → NSXPCInterface (in SDK set)
    public void IsApiDefinitionTypeResolvable_WithSdkNames_HandlesAcronymConvention(string mappedType, bool expected)
    {
        var knownTypes = ObjCTypeMapper.BuildKnownMappedTypes();
        var sdkNames = new HashSet<string> { "NSHTTPURLResponse", "NSURLSession", "NSURLSessionDelegate", "UIColor", "NSXPCConnection", "NSXPCInterface" };
        Assert.Equal(expected, ObjCTypeMapper.IsApiDefinitionTypeResolvable(mappedType, knownTypes, sdkNames));
    }

    // Direct coverage of the reverse-acronym helper, including roundtrip with Apply.
    [Theory]
    [InlineData("NSHttpUrlResponse", "NSHTTPURLResponse")]
    [InlineData("NSXpcConnection", "NSXPCConnection")]
    [InlineData("NSJsonSerialization", "NSJSONSerialization")]
    [InlineData("NSHtmlParser", "NSHTMLParser")]
    [InlineData("UIColor", "UIColor")]                 // non-NS prefix → unchanged
    [InlineData("NSObject", "NSObject")]               // no acronym substring → unchanged
    public void ReverseDotNetAcronymConvention_ReversesPascalToAllCaps(string mapped, string expected)
    {
        Assert.Equal(expected, ObjCTypeMapper.ReverseDotNetAcronymConvention(mapped));
    }

    [Theory]
    [InlineData("NSHTTPURLResponse")]
    [InlineData("NSXPCConnection")]
    [InlineData("NSJSONSerialization")]
    [InlineData("NSHTMLParser")]
    public void AcronymConvention_RoundTrip_ApplyThenReverse_RestoresOriginal(string original)
    {
        var applied = ObjCTypeMapper.ApplyDotNetAcronymConvention(original);
        Assert.Equal(original, ObjCTypeMapper.ReverseDotNetAcronymConvention(applied));
    }

    [Theory]
    [InlineData("UIColor", true)]          // Apple ObjC prefix "UI" → accepted
    [InlineData("NSObject", true)]         // Listed in BuildKnownMappedTypes
    [InlineData("AnyObjCType", false)]     // Uppercase but no registered Apple prefix → rejected (was permissive false-positive that produced CS0246 for cross-framework third-party types)
    [InlineData("pb_wire_type_t", false)]  // Lowercase → rejected
    public void IsApiDefinitionTypeResolvable_NullSdkNames_FallsBackToHeuristic(string mappedType, bool expected)
    {
        var knownTypes = ObjCTypeMapper.BuildKnownMappedTypes();
        Assert.Equal(expected, ObjCTypeMapper.IsApiDefinitionTypeResolvable(mappedType, knownTypes, null));
    }

    // ──────────────────────────────────────────────
    // ResolutionTypedefs — non-framework typedef resolution
    // ──────────────────────────────────────────────

    [Fact]
    public void BuildResolvedTypedefMap_UsesResolutionTypedefs()
    {
        // Simulates a typedef from a system header (not framework-local) that should
        // be available for resolution but not emitted.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Typedefs = [], // No framework-local typedefs
            ResolutionTypedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "my_alias_t",
                    UnderlyingType = new ObjCTypeRef { Name = "uint32_t" }
                }
            ]
        };

        var typedefMap = ObjCTypeMapper.BuildResolvedTypedefMap(module);
        Assert.True(typedefMap.ContainsKey("my_alias_t"));
        // Should resolve through to uint (uint32_t → uint)
        var result = ObjCTypeMapper.MapType(new ObjCTypeRef { Name = "my_alias_t" }, typedefMap: typedefMap);
        Assert.Equal("uint", result);
    }

    // --- Block nullability → IsNullableAttribute (end-to-end from parser) ---

    [Fact]
    public void IsNullableAttribute_NullableBlock_ReturnsTrue()
    {
        var result = ObjCTypeRefParser.Parse("void (^ _Nullable)(NSString *)");
        Assert.True(ObjCTypeMapper.IsNullableAttribute(result));
    }

    [Fact]
    public void IsNullableAttribute_NonnullBlock_ReturnsFalse()
    {
        var result = ObjCTypeRefParser.Parse("void (^ _Nonnull)(NSString *)");
        Assert.False(ObjCTypeMapper.IsNullableAttribute(result));
    }

    // Generic collection type hints

    [Fact]
    public void FormatGenericTypeHint_NSArray_ReturnsElementType()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSArray",
            IsPointer = true,
            GenericArgs = [new ObjCTypeRef { Name = "NSString", IsPointer = true }]
        };
        Assert.Equal("Element type: string", ObjCTypeMapper.FormatGenericTypeHint(typeRef));
    }

    [Fact]
    public void FormatGenericTypeHint_NSDictionary_ReturnsKeyValueTypes()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSDictionary",
            IsPointer = true,
            GenericArgs =
            [
                new ObjCTypeRef { Name = "NSString", IsPointer = true },
                new ObjCTypeRef { Name = "NSNumber", IsPointer = true }
            ]
        };
        Assert.Equal("Key type: string, Value type: NSNumber", ObjCTypeMapper.FormatGenericTypeHint(typeRef));
    }

    [Fact]
    public void FormatGenericTypeHint_NSSet_ReturnsElementType()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSSet",
            IsPointer = true,
            GenericArgs = [new ObjCTypeRef { Name = "NSNumber", IsPointer = true }]
        };
        Assert.Equal("Element type: NSNumber", ObjCTypeMapper.FormatGenericTypeHint(typeRef));
    }

    [Fact]
    public void FormatGenericTypeHint_NoGenericArgs_ReturnsNull()
    {
        var typeRef = new ObjCTypeRef { Name = "NSArray", IsPointer = true };
        Assert.Null(ObjCTypeMapper.FormatGenericTypeHint(typeRef));
    }

    [Fact]
    public void FormatGenericTypeHint_WithNullability_IncludesAnnotation()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSArray",
            IsPointer = true,
            Nullability = ObjCNullability.Nonnull,
            GenericArgs = [new ObjCTypeRef { Name = "NSString", IsPointer = true, Nullability = ObjCNullability.Nullable }]
        };
        Assert.Equal("Element type: string (nullable)", ObjCTypeMapper.FormatGenericTypeHint(typeRef));
    }

    // --- Typed generic collection mapping (NSArray<T> → T[], NSDictionary<K,V> → NSDictionary<K,V>) ---

    [Fact]
    public void MapType_NSArrayWithStringGenericArg_ReturnsStringArray()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSArray",
            IsPointer = true,
            GenericArgs = [new ObjCTypeRef { Name = "NSString", IsPointer = true }]
        };
        Assert.Equal("string[]", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_NSArrayWithCustomTypeGenericArg_ReturnsTypedArray()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSArray",
            IsPointer = true,
            GenericArgs = [new ObjCTypeRef { Name = "LabelPrinterLog", IsPointer = true }]
        };
        Assert.Equal("LabelPrinterLog[]", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_NSArrayWithURLGenericArg_ReturnsNSUrlArray()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSArray",
            IsPointer = true,
            GenericArgs = [new ObjCTypeRef { Name = "NSURL", IsPointer = true }]
        };
        Assert.Equal("NSUrl[]", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_NSArrayWithGenericTypeParam_FallsBackToNSArray()
    {
        // When element is a generic type parameter (e.g., ObjectType), fall through to plain NSArray
        var typeRef = new ObjCTypeRef
        {
            Name = "NSArray",
            IsPointer = true,
            GenericArgs = [new ObjCTypeRef { Name = "ObjectType" }]
        };
        var genericParams = new HashSet<string> { "ObjectType" };
        Assert.Equal("NSArray", ObjCTypeMapper.MapType(typeRef, genericTypeParams: genericParams));
    }

    [Fact]
    public void MapType_NSMutableArrayWithGenericArg_ReturnsTypedArray()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSMutableArray",
            IsPointer = true,
            GenericArgs = [new ObjCTypeRef { Name = "NSData", IsPointer = true }]
        };
        Assert.Equal("NSData[]", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_NSDictionaryWithGenericArgs_ReturnsTypedDictionary()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSDictionary",
            IsPointer = true,
            GenericArgs =
            [
                new ObjCTypeRef { Name = "NSString", IsPointer = true },
                new ObjCTypeRef { Name = "NSNumber", IsPointer = true }
            ]
        };
        Assert.Equal("NSDictionary<NSString, NSNumber>", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_NSDictionaryWithGenericParam_FallsBackToNSDictionary()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSDictionary",
            IsPointer = true,
            GenericArgs =
            [
                new ObjCTypeRef { Name = "KeyType" },
                new ObjCTypeRef { Name = "ValueType" }
            ]
        };
        var genericParams = new HashSet<string> { "KeyType", "ValueType" };
        Assert.Equal("NSDictionary", ObjCTypeMapper.MapType(typeRef, genericTypeParams: genericParams));
    }

    [Fact]
    public void MapType_NSMutableDictionaryWithGenericArgs_ReturnsTypedDictionary()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSMutableDictionary",
            IsPointer = true,
            GenericArgs =
            [
                new ObjCTypeRef { Name = "NSString", IsPointer = true },
                new ObjCTypeRef { Name = "NSURL", IsPointer = true }
            ]
        };
        Assert.Equal("NSDictionary<NSString, NSUrl>", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_NSSetWithGenericArg_ReturnsTypedNSSet()
    {
        var typeRef = new ObjCTypeRef
        {
            Name = "NSSet",
            IsPointer = true,
            GenericArgs = [new ObjCTypeRef { Name = "NSString", IsPointer = true }]
        };
        Assert.Equal("NSSet<NSString>", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_NSArrayWithBlockElement_FallsBackToNSArray()
    {
        // NSArray<block_type> — closures don't implement INativeObject
        var typeRef = new ObjCTypeRef
        {
            Name = "NSArray",
            IsPointer = true,
            GenericArgs = [new ObjCTypeRef
            {
                Name = "Block",
                IsBlock = true,
                BlockReturnType = new ObjCTypeRef { Name = "CGImage", IsPointer = true },
                BlockParams = []
            }]
        };
        Assert.Equal("NSArray", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_NSArrayWithNestedNSArray_FallsBackToNSArray()
    {
        // NSArray<NSArray<T>> — nested arrays don't implement INativeObject
        var typeRef = new ObjCTypeRef
        {
            Name = "NSArray",
            IsPointer = true,
            GenericArgs = [new ObjCTypeRef
            {
                Name = "NSArray",
                IsPointer = true,
                GenericArgs = [new ObjCTypeRef { Name = "MOSGeospatialPoint", IsPointer = true }]
            }]
        };
        Assert.Equal("NSArray", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_NSArrayWithActionPrefixedType_ReturnsTypedArray()
    {
        // ObjC types like ActionCodeSettings start with "Action" but are valid INativeObject types
        var typeRef = new ObjCTypeRef
        {
            Name = "NSArray",
            IsPointer = true,
            GenericArgs = [new ObjCTypeRef { Name = "ActionCodeSettings", IsPointer = true }]
        };
        Assert.Equal("ActionCodeSettings[]", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_NSArrayWithoutGenericArgs_StillReturnsNSArray()
    {
        // Plain NSArray (no generic args) should still map to NSArray as before
        var typeRef = new ObjCTypeRef { Name = "NSArray", IsPointer = true };
        Assert.Equal("NSArray", ObjCTypeMapper.MapType(typeRef));
    }

    [Fact]
    public void MapType_NSDictionaryWithoutGenericArgs_StillReturnsNSDictionary()
    {
        var typeRef = new ObjCTypeRef { Name = "NSDictionary", IsPointer = true };
        Assert.Equal("NSDictionary", ObjCTypeMapper.MapType(typeRef));
    }

    // --- IsKnownMappedOrPatternType for new generic patterns ---

    [Theory]
    [InlineData("string[]", true)]
    [InlineData("NSUrl[]", true)]
    [InlineData("NSDictionary<string, NSNumber>", true)]
    [InlineData("NSSet<string>", true)]
    public void IsApiDefinitionTypeResolvable_TypedGenericPatterns_AreResolvable(string mappedType, bool expected)
    {
        var knownTypes = ObjCTypeMapper.BuildKnownMappedTypes();
        Assert.Equal(expected, ObjCTypeMapper.IsApiDefinitionTypeResolvable(mappedType, knownTypes, null));
    }

    // --- Resolvability recurses into wrapper arguments ---
    // A wrapper (Action<…>, Func<…>, NSDictionary<K,V>, T[]) is a known pattern, but an absent
    // argument type nested inside it would still emit and fail CS0246, so the gate must recurse.

    [Theory]
    [InlineData("Action<NSError>", true)]                              // resolvable block arg — kept
    [InlineData("Func<string, bool>", true)]                           // primitives inside — kept
    [InlineData("byte[]", true)]                                       // primitive array element — kept
    [InlineData("NSDictionary<string, NSError>", true)]               // resolvable value type — kept
    [InlineData("Action<ZZThirdPartyType, NSError>", false)]          // absent block arg — dropped
    [InlineData("ZZThirdPartyType[]", false)]                         // absent array element — dropped
    [InlineData("NSDictionary<string, ZZThirdPartyType>", false)]    // absent value type — dropped
    [InlineData("Action<NSDictionary<string, ZZThirdPartyType>>", false)] // absent nested deeper — dropped
    public void IsApiDefinitionTypeResolvable_RecursesIntoWrapperArguments(string mappedType, bool expected)
    {
        var knownTypes = ObjCTypeMapper.BuildKnownMappedTypes();
        // SDK-name mode (mirrors a textual clang parse): NSError is an available SDK type;
        // ZZThirdPartyType is a genuinely-absent cross-module class with no using/declaration.
        var appleSdkTypeNames = new HashSet<string>(StringComparer.Ordinal) { "NSError" };
        Assert.Equal(expected, ObjCTypeMapper.IsApiDefinitionTypeResolvable(mappedType, knownTypes, appleSdkTypeNames));
    }
}
