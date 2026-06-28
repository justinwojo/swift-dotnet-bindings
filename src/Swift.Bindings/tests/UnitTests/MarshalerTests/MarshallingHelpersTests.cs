// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MarshallingHelpers utility methods.
/// </summary>
public class MarshallingHelpersTests
{
    #region MethodIsSetter Tests

    [Fact]
    public void MethodIsSetter_ReturnsTrueForSetterMethod()
    {
        var method = CreateMethodDecl("myProperty_Set");
        Assert.True(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsTrueForSetterWithUnderscoreInName()
    {
        var method = CreateMethodDecl("my_Property_Set");
        Assert.True(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsFalseForGetterMethod()
    {
        var method = CreateMethodDecl("myProperty_Get");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsFalseForRegularMethod()
    {
        var method = CreateMethodDecl("doSomething");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsFalseForMethodEndingInSet()
    {
        // "Set" without underscore is not a setter
        var method = CreateMethodDecl("resetSet");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsFalseForMethodContainingSetInMiddle()
    {
        var method = CreateMethodDecl("set_something");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_IsCaseSensitive()
    {
        // "_set" (lowercase) should not match
        var method = CreateMethodDecl("myProperty_set");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    #endregion

    #region IsObjCBridged Tests

    [Fact]
    public void IsObjCBridged_ReturnsTrueWhenFlagIsSet()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.ObjCBridged);
        Assert.True(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    [Fact]
    public void IsObjCBridged_ReturnsFalseWhenFlagIsNotSet()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.None);
        Assert.False(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    [Fact]
    public void IsObjCBridged_ReturnsTrueWhenObjCBridgedCombinedWithOtherFlags()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement);
        Assert.True(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    [Fact]
    public void IsObjCBridged_ReturnsFalseForFrozenType()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.Frozen);
        Assert.False(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    [Fact]
    public void IsObjCBridged_ReturnsFalseForRequiresMemoryManagement()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.RequiresMemoryManagement);
        Assert.False(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    #endregion

    #region MethodRequiresIndirectResult Tests

    [Fact]
    public void MethodRequiresIndirectResult_AsyncMethod_ReturnsFalse()
    {
        // Async methods never need indirect result
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("Swift.Int"), isAsync: true);
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_FailableConstructor_ReturnsTrue()
    {
        // Failable constructors (init?) always need indirect result for Optional<Self> checking
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("Swift.Int"),
            isConstructor: true,
            isFailable: true,
            parentDecl: CreateFrozenStructParent());
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_NonFrozenConstructor_ReturnsTrue()
    {
        // Non-frozen struct constructors need indirect result
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("Swift.Int"),
            isConstructor: true,
            parentDecl: CreateNonFrozenStructParent());
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_FrozenStructConstructor_ReturnsFalse()
    {
        // Frozen struct constructors return in-register
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("Swift.Int"),
            isConstructor: true,
            parentDecl: CreateFrozenStructParent());
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_CdeclFrozenStructConstructor_ReturnsTrue()
    {
        // @_cdecl frozen struct constructors write to resultPtr, not return by value.
        // The Swift @_cdecl wrapper takes UnsafeMutableRawPointer as the first parameter.
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("Swift.Int"),
            isConstructor: true,
            usesCdeclConstructorWrapper: true,
            parentDecl: CreateFrozenStructParent());
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_CdeclClassConstructor_ReturnsFalse()
    {
        // @_cdecl class constructors return a pointer directly, not via resultPtr.
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("TestModule.MyClass"),
            isConstructor: true,
            usesCdeclConstructorWrapper: true,
            parentDecl: CreateClassParent());
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_ClassConstructor_ReturnsFalse()
    {
        // Class constructors return a pointer in-register, not via indirect result
        var env = CreateMethodEnv(
            returnType: new NamedTypeSpec("TestModule.MyClass"),
            isConstructor: true,
            parentDecl: CreateClassParent());
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_ClosureReturn_ReturnsFalse()
    {
        // Closure return types are passed as function pointers, not indirectly
        var closureReturn = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var env = CreateMethodEnv(returnType: closureReturn);
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_ExistentialReturn_ReturnsFalse()
    {
        // Existential types (any Protocol) are passed via existential containers (IntPtr)
        var existentialReturn = new ProtocolListTypeSpec();
        var env = CreateMethodEnv(returnType: existentialReturn);
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_NonGenericTupleReturn_ReturnsFalse()
    {
        // Non-generic tuples are handled by TupleHandler, not via indirect result
        var tupleReturn = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var env = CreateMethodEnv(returnType: tupleReturn);
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_TupleWithGenericElements_ReturnsTrue()
    {
        // Tuples with generic type parameter elements require indirect result
        var tupleReturn = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("τ_0_0"),
            new NamedTypeSpec("Swift.Int")
        });
        var env = CreateMethodEnv(returnType: tupleReturn);
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_BoundGenericWithMarshalling_ReturnsFalse()
    {
        // Bound generics that require marshalling (SwiftArray, SwiftOptional) return IntPtr directly
        var arrayReturn = new NamedTypeSpec("Swift.Array");
        arrayReturn.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var env = CreateMethodEnv(returnType: arrayReturn);
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_GenericReturn_ReturnsTrue()
    {
        // Generic return types need indirect result because sizes are unknown
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("Swift.Int"), isGenericReturn: true);
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_ClassReturn_ReturnsFalse()
    {
        // Swift classes return pointers directly in registers
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("TestModule.MyClass"));
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_FrozenStructReturn_ReturnsFalse()
    {
        // Frozen structs fit in registers
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("Swift.Int"));
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_NonFrozenStructReturn_ReturnsTrue()
    {
        // Non-frozen types need indirect result
        var env = CreateMethodEnv(returnType: new NamedTypeSpec("TestModule.NonFrozen"));
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_DynamicSelfReturn_ReturnsTrue()
    {
        // A1 DynamicSelf hardening: "Self" return type always requires indirect result.
        // The explicit IsDynamicSelf guard fires early (before GetTypeRecordOrThrow).
        // Companion test TryGetAnyTypeFallbackInfo_DynamicSelf_IsNotFallback verifies
        // DynamicSelf is NOT classified as an existential — that test would fail if
        // the explicit guard were removed.
        var selfReturn = new NamedTypeSpec("Self");
        Assert.True(selfReturn.IsDynamicSelf);
        var env = CreateMethodEnv(returnType: selfReturn);
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_CdeclDynamicSelfReturn_ReturnsFalse()
    {
        // @_cdecl wrapper returns DynamicSelf as retained class pointer (UnsafeMutableRawPointer).
        // P/Invoke receives IntPtr directly — NOT indirect result.
        var selfReturn = new NamedTypeSpec("Self");
        var env = CreateMethodEnv(returnType: selfReturn);
        env.MethodDecl.UsesCdeclMethodWrapper = true;
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_CdeclNonGenericTupleReturn_ReturnsTrue()
    {
        // @_cdecl wrapper writes tuple to resultPtr buffer — tuples aren't C-representable.
        // Non-@_cdecl path returns false (handled by TupleHandler), but @_cdecl needs indirect result.
        var tupleReturn = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var env = CreateMethodEnv(returnType: tupleReturn);
        env.MethodDecl.UsesCdeclMethodWrapper = true;
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_CdeclArrayReturn_ReturnsTrue()
    {
        // @_cdecl method returning Swift.Array<T> must use indirect result.
        // Swift wrapper writes to resultPtr via initializeMemory(as:). Without this fix,
        // the P/Invoke returned IntPtr (the non-@_cdecl bound generic path), causing
        // a signature mismatch with the void+resultPtr Swift wrapper.
        var arrayReturn = new NamedTypeSpec("Swift.Array");
        arrayReturn.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var env = CreateMethodEnv(returnType: arrayReturn);
        env.MethodDecl.UsesCdeclMethodWrapper = true;
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_NonCdeclArrayReturn_ReturnsFalse()
    {
        // Inverse: non-@_cdecl Array returns use IntPtr directly (bound generic path).
        var arrayReturn = new NamedTypeSpec("Swift.Array");
        arrayReturn.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var env = CreateMethodEnv(returnType: arrayReturn);
        // No UsesCdeclMethodWrapper set — falls through to bound generic path
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_CdeclBoundGenericClassReturn_ReturnsFalse()
    {
        // Bound generic CLASS returns (e.g., `Mapper<T>`) use the ClassPointer convention:
        // the @_cdecl wrapper returns `Unmanaged.passRetained(... as AnyObject).toOpaque()` as
        // `UnsafeMutableRawPointer`, and the C# P/Invoke must receive IntPtr directly — NOT via
        // an indirect resultPtr buffer. CdeclReturnMapping.Classify already picks ClassPointer
        // for typeRecord.Kind == Class; IsCdeclIndirectResultRequired must agree (the two
        // emitters have to match or factory dispose crashes on swift_release of a garbage ptr).
        var classReturn = new NamedTypeSpec("TestModule.MyClass");
        classReturn.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var env = CreateMethodEnv(returnType: classReturn);
        env.MethodDecl.UsesCdeclMethodWrapper = true;
        Assert.False(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    #endregion

    #region IsCoreFoundationType Tests

    [Theory]
    [InlineData("CoreText.CTFont", true)]
    [InlineData("CoreGraphics.CGColor", true)]
    [InlineData("CoreImage.CIFilter", true)]
    [InlineData("CoreAnimation.CALayer", true)]
    [InlineData("CoreMedia.CMTime", true)]
    [InlineData("CoreVideo.CVPixelBuffer", true)]
    [InlineData("Security.SecKey", true)]
    [InlineData("CoreFoundation.CFString", true)]
    [InlineData("UIKit.UIImage", false)]
    [InlineData("Foundation.NSObject", false)]
    [InlineData("AppKit.NSView", false)]
    public void IsCoreFoundationType_CategorizesCorrectly(string typeName, bool expected)
    {
        Assert.Equal(expected, MarshallingHelpers.IsCoreFoundationType(typeName));
    }

    #endregion

    #region FormatObjCBridgeCall Tests

    [Fact]
    public void FormatObjCBridgeCall_NSObjectType_UsesGetNSObject()
    {
        var result = MarshallingHelpers.FormatObjCBridgeCall("UIKit.UIImage", "result", nonNull: true);
        Assert.Equal("ObjCRuntime.Runtime.GetNSObject<UIKit.UIImage>(result)!", result);
    }

    [Fact]
    public void FormatObjCBridgeCall_CoreFoundationType_UsesGetINativeObject()
    {
        var result = MarshallingHelpers.FormatObjCBridgeCall("CoreText.CTFont", "result", nonNull: false);
        Assert.Equal("ObjCRuntime.Runtime.GetINativeObject<CoreText.CTFont>(result, false)", result);
    }

    [Fact]
    public void FormatObjCBridgeCall_CoreFoundationType_WithNonNull_AppendsExclamation()
    {
        var result = MarshallingHelpers.FormatObjCBridgeCall("CoreGraphics.CGColor", "ptr", nonNull: true);
        Assert.Equal("ObjCRuntime.Runtime.GetINativeObject<CoreGraphics.CGColor>(ptr, false)!", result);
    }

    [Fact]
    public void FormatObjCBridgeCall_NSObjectType_WithoutNonNull_NoExclamation()
    {
        var result = MarshallingHelpers.FormatObjCBridgeCall("Foundation.NSData", "result");
        Assert.Equal("ObjCRuntime.Runtime.GetNSObject<Foundation.NSData>(result)", result);
    }

    [Fact]
    public void FormatObjCBridgeCall_NSObjectType_OwnsReference_UsesGetINativeObject()
    {
        // ownsReference=true on NSObject types should use GetINativeObject (not GetNSObject)
        // to transfer +1 ownership without adding an extra DangerousRetain
        var result = MarshallingHelpers.FormatObjCBridgeCall("UIKit.UIImage", "ptr", ownsReference: true);
        Assert.Equal("ObjCRuntime.Runtime.GetINativeObject<UIKit.UIImage>(ptr, true)", result);
    }

    [Fact]
    public void FormatObjCBridgeCall_NSObjectType_OwnsReference_WithNonNull_AppendsExclamation()
    {
        var result = MarshallingHelpers.FormatObjCBridgeCall("Foundation.NSData", "ptr", nonNull: true, ownsReference: true);
        Assert.Equal("ObjCRuntime.Runtime.GetINativeObject<Foundation.NSData>(ptr, true)!", result);
    }

    #endregion

    #region IsOptionalObjCBridged — System Framework Fallback Tests

    [Fact]
    public void IsOptionalObjCBridged_SystemFrameworkObjCClass_ReturnsTrueViaAppleModuleFallback()
    {
        // QuartzCore.CALayer is not in the mock TypeDatabase, but the Apple module fallback
        // matches: IsKnownAppleModule("QuartzCore") + HasObjCClassPrefix("QuartzCore.CALayer").
        var typeSpec = TypeSpecParser.Parse("Swift.Optional<QuartzCore.CALayer>");
        var db = new MockTypeDatabase();
        Assert.True(MarshallingHelpers.IsOptionalObjCBridged(typeSpec, db));
    }

    [Fact]
    public void IsOptionalObjCBridged_ObjCBridgedInDatabase_ReturnsTrue()
    {
        // Type IS in the database with ObjCBridged flag — existing behavior still works.
        var typeSpec = TypeSpecParser.Parse("Swift.Optional<UIKit.UIImage>");
        var db = new MockTypeDatabaseWithObjC();
        Assert.True(MarshallingHelpers.IsOptionalObjCBridged(typeSpec, db));
    }

    [Fact]
    public void IsOptionalObjCBridged_ObjCRootedInDatabase_ReturnsFalse()
    {
        // ObjCRooted types use SwiftOptional<T> marshalling (NOT IntPtr nullable pointer ABI).
        // PropertyHandler's GetOptionalAccessorSetterConversion dispatches ObjCRootedClassProjection
        // to SwiftOptional<T>.NewSome, not IntPtr. IsOptionalObjCBridged must NOT return true.
        var typeSpec = TypeSpecParser.Parse("Swift.Optional<TestModule.ObjCRooted>");
        var db = new MockTypeDatabaseWithObjC();
        Assert.False(MarshallingHelpers.IsOptionalObjCBridged(typeSpec, db));
    }

    [Fact]
    public void IsOptionalObjCBridged_NonObjCType_ReturnsFalse()
    {
        // Non-ObjC type in the database — returns false.
        var typeSpec = TypeSpecParser.Parse("Swift.Optional<TestModule.NonFrozen>");
        var db = new MockTypeDatabase();
        Assert.False(MarshallingHelpers.IsOptionalObjCBridged(typeSpec, db));
    }

    [Fact]
    public void IsOptionalObjCBridged_UIKitObjCClass_ReturnsTrueViaFallback()
    {
        // CQ-7: UIKit.UIFont is not in the mock TypeDatabase, but the Apple module fallback
        // now matches: IsKnownAppleModule("UIKit") + HasObjCClassPrefix("UIKit.UIFont").
        var typeSpec = TypeSpecParser.Parse("Swift.Optional<UIKit.UIFont>");
        var db = new MockTypeDatabase();
        Assert.True(MarshallingHelpers.IsOptionalObjCBridged(typeSpec, db));
    }

    [Fact]
    public void IsOptionalObjCBridged_NestedUIKitType_ReturnsFalse()
    {
        // Nested types like UIControl.State are structs/enums — the IsNestedType guard
        // prevents the Apple module fallback from misidentifying them as ObjC classes.
        var typeSpec = TypeSpecParser.Parse("Swift.Optional<UIKit.UIControl.State>");
        var db = new MockTypeDatabase();
        Assert.False(MarshallingHelpers.IsOptionalObjCBridged(typeSpec, db));
    }

    [Fact]
    public void IsOptionalObjCBridged_CoreMotionValueStruct_ReturnsFalse()
    {
        // CoreMotion is an OptionalFallback module with objcPrefix "CM", but CMAcceleration
        // is a genuine C value struct registered in valueTypes. The IsKnownAppleValueType
        // guard must veto the weak ObjC-class fallback so Optional<CMAcceleration> is NOT
        // marshalled as ObjCBridged (which would emit GetINativeObject<CMAcceleration>/.Handle —
        // CS0315/CS1061, the RealityFoundation regression).
        var typeSpec = TypeSpecParser.Parse("Swift.Optional<CoreMotion.CMAcceleration>");
        var db = new MockTypeDatabase();
        Assert.False(MarshallingHelpers.IsOptionalObjCBridged(typeSpec, db));
    }

    /// <summary>Mock database with ObjC type records for IsOptionalObjCBridged tests.</summary>
    private class MockTypeDatabaseWithObjC : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types = new()
        {
            ["UIKit.UIImage"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIImage"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIImage"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            },
            ["TestModule.ObjCRooted"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ObjCRooted"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ObjCRooted"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCRooted | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            }
        };

        public string? AsyncLibraryName => null;
        public bool IsTypeProcessed(SwiftTypeName s) => _types.ContainsKey(s.ModuleQualifiedName);
        public bool TryGetTypeRecord(SwiftTypeName s, [NotNullWhen(true)] out TypeRecord? r) => _types.TryGetValue(s.ModuleQualifiedName, out r);
        public string GetLibraryPath(string m) => "";
        public void UpdateTypeRecord(SwiftTypeName n, TypeRecord r) { }
    }

    #endregion

    #region SwiftModule → .NET Namespace Mapping

    [Fact]
    public void MapSwiftModuleToNetNamespace_QuartzCore_ReturnsCoreAnimation()
    {
        Assert.Equal("CoreAnimation", MarshallingHelpers.MapSwiftModuleToNetNamespace("QuartzCore"));
    }

    [Fact]
    public void MapSwiftModuleToNetNamespace_Dispatch_ReturnsCoreFoundation()
    {
        Assert.Equal("CoreFoundation", MarshallingHelpers.MapSwiftModuleToNetNamespace("Dispatch"));
    }

    [Fact]
    public void MapSwiftModuleToNetNamespace_AVFAudio_ReturnsAVFoundation()
    {
        Assert.Equal("AVFoundation", MarshallingHelpers.MapSwiftModuleToNetNamespace("AVFAudio"));
    }

    [Fact]
    public void MapSwiftModuleToNetNamespace_UnmappedModule_ReturnsPassthrough()
    {
        Assert.Equal("UIKit", MarshallingHelpers.MapSwiftModuleToNetNamespace("UIKit"));
    }

    [Fact]
    public void MapQualifiedTypeToNet_MapsModulePrefix()
    {
        Assert.Equal("CoreAnimation.CALayer", MarshallingHelpers.MapQualifiedTypeToNet("QuartzCore.CALayer"));
    }

    [Fact]
    public void MapQualifiedTypeToNet_UnmappedModule_ReturnsUnchanged()
    {
        Assert.Equal("UIKit.UIView", MarshallingHelpers.MapQualifiedTypeToNet("UIKit.UIView"));
    }

    #endregion

    #region Helper Methods

    private static MethodDecl CreateMethodDecl(string name)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = true
        };
    }

    private static TypeRecord CreateTypeRecord(TypeRecordFlags flags)
    {
        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "TestType"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.TestType"),
            MetadataAccessor = "testAccessor",
            Flags = flags,
            Kind = TypeRecordKind.Class
        };
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static StructDecl CreateFrozenStructParent()
    {
        var moduleDecl = CreateModuleDecl();
        return new StructDecl
        {
            Name = "TestStruct",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestStruct"),
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
    }

    private static StructDecl CreateNonFrozenStructParent()
    {
        var moduleDecl = CreateModuleDecl();
        return new StructDecl
        {
            Name = "NonFrozenStruct",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.NonFrozenStruct"),
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = CreateModuleDecl(),
            ModuleDecl = CreateModuleDecl(),
            IsFrozen = false,
            MetadataAccessor = "$sMa"
        };
    }

    private static ClassDecl CreateClassParent()
    {
        var moduleDecl = CreateModuleDecl();
        return new ClassDecl
        {
            Name = "TestClass",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestClass"),
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static MethodEnvironment CreateMethodEnv(
        TypeSpec returnType,
        bool isAsync = false,
        bool isConstructor = false,
        bool isFailable = false,
        bool isGenericReturn = false,
        bool usesCdeclConstructorWrapper = false,
        BaseDecl? parentDecl = null)
    {
        var moduleDecl = CreateModuleDecl();
        parentDecl ??= moduleDecl;

        var method = new MethodDecl
        {
            Name = isConstructor ? "init" : "testMethod",
            MangledName = "$sTest",
            MethodType = MethodType.Instance,
            IsConstructor = isConstructor,
            IsFailable = isFailable,
            UsesCdeclConstructorWrapper = usesCdeclConstructorWrapper,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = returnType,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = isGenericReturn,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = isAsync,
            IsSynthesizedAccessor = false
        };

        return new MethodEnvironment(method, new MockTypeDatabase());
    }

    #endregion

    #region MockTypeDatabase

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string? AsyncLibraryName => null;

        public MockTypeDatabase()
        {
            _types = new Dictionary<string, TypeRecord>
            {
                ["Swift.Int"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.NIntType,
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Bool"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.String"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Array"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Optional"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["TestModule.MyClass"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class
                },
                ["TestModule.NonFrozen"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "NonFrozen"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.NonFrozen"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Struct
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion

    #region MapModulesInString Tests (CQ-1)

    [Theory]
    [InlineData("QuartzCore.CALayerContentsGravity", "CoreAnimation.CALayerContentsGravity")]
    [InlineData("ObjectiveC.NSObject", "Foundation.NSObject")]
    [InlineData("Dispatch.DispatchQueue", "CoreFoundation.DispatchQueue")]
    [InlineData("AVFAudio.AVAudioPlayer", "AVFoundation.AVAudioPlayer")]
    public void MapModulesInString_MapsKnownModules(string input, string expected)
    {
        Assert.Equal(expected, MarshallingHelpers.MapModulesInString(input));
    }

    [Theory]
    [InlineData("UIKit.UIImage")]
    [InlineData("Swift.Int")]
    [InlineData("NoModule")]
    [InlineData("")]
    public void MapModulesInString_LeavesUnmappedModulesUnchanged(string input)
    {
        Assert.Equal(input, MarshallingHelpers.MapModulesInString(input));
    }

    [Fact]
    public void MapModulesInString_MapsModulesInDiagnosticString()
    {
        var input = "return type 'QuartzCore.CALayerContentsGravity' is not dispatchable";
        var expected = "return type 'CoreAnimation.CALayerContentsGravity' is not dispatchable";
        Assert.Equal(expected, MarshallingHelpers.MapModulesInString(input));
    }

    #endregion

    #region UnsafeRawBufferPointer / UnsafeMutableRawBufferPointer Predicates

    [Fact]
    public void IsUnsafeRawBufferPointer_ReadOnlyVariant_ReturnsTrue()
    {
        var spec = new NamedTypeSpec("Swift.UnsafeRawBufferPointer");
        Assert.True(MarshallingHelpers.IsUnsafeRawBufferPointer(spec));
        Assert.False(MarshallingHelpers.IsUnsafeMutableRawBufferPointer(spec));
        Assert.True(MarshallingHelpers.IsAnyUnsafeRawBufferPointer(spec));
    }

    [Fact]
    public void IsUnsafeMutableRawBufferPointer_MutableVariant_ReturnsTrue()
    {
        var spec = new NamedTypeSpec("Swift.UnsafeMutableRawBufferPointer");
        Assert.False(MarshallingHelpers.IsUnsafeRawBufferPointer(spec));
        Assert.True(MarshallingHelpers.IsUnsafeMutableRawBufferPointer(spec));
        Assert.True(MarshallingHelpers.IsAnyUnsafeRawBufferPointer(spec));
    }

    [Theory]
    [InlineData("Swift.UnsafePointer")]
    [InlineData("Swift.UnsafeMutablePointer")]
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Swift.UnsafeBufferPointer")]
    [InlineData("Swift.UnsafeMutableBufferPointer")]
    [InlineData("Swift.Int")]
    [InlineData("Swift.String")]
    public void RawBufferPointerPredicates_OtherPointerTypes_ReturnFalse(string typeName)
    {
        // Bare pointers and typed buffer pointers are explicitly NOT raw buffer pointers.
        // Bare UnsafeMutableRawPointer in particular has no length and cannot project to
        // Span<byte> — bare pointers lack a length component and do not split to (ptr, len).
        var spec = new NamedTypeSpec(typeName);
        Assert.False(MarshallingHelpers.IsUnsafeRawBufferPointer(spec));
        Assert.False(MarshallingHelpers.IsUnsafeMutableRawBufferPointer(spec));
        Assert.False(MarshallingHelpers.IsAnyUnsafeRawBufferPointer(spec));
    }

    [Fact]
    public void RawBufferPointerPredicates_NullSpec_ReturnsFalse()
    {
        Assert.False(MarshallingHelpers.IsUnsafeRawBufferPointer(null));
        Assert.False(MarshallingHelpers.IsUnsafeMutableRawBufferPointer(null));
        Assert.False(MarshallingHelpers.IsAnyUnsafeRawBufferPointer(null));
    }

    #endregion

    #region IsSwiftResult Tests

    [Fact]
    public void IsSwiftResult_BoundResult_ReturnsTrue()
    {
        // Swift.Result<LoadedAsset, any Error> — the matcher keys on the module-qualified
        // name, independent of the (success, failure) generic arguments.
        var result = new NamedTypeSpec("Swift.Result");
        result.GenericParameters.Add(new NamedTypeSpec("TestModule.LoadedAsset"));
        result.GenericParameters.Add(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") }));

        Assert.True(MarshallingHelpers.IsSwiftResult(result));
    }

    [Fact]
    public void IsSwiftResult_BareResult_ReturnsTrue()
    {
        Assert.True(MarshallingHelpers.IsSwiftResult(new NamedTypeSpec("Swift.Result")));
    }

    [Fact]
    public void IsSwiftResult_Optional_ReturnsFalse()
    {
        // Sibling stdlib generic — must not be mistaken for Result.
        Assert.False(MarshallingHelpers.IsSwiftResult(new NamedTypeSpec("Swift.Optional")));
    }

    [Fact]
    public void IsSwiftResult_UnrelatedNamedType_ReturnsFalse()
    {
        Assert.False(MarshallingHelpers.IsSwiftResult(new NamedTypeSpec("Swift.Int")));
    }

    [Fact]
    public void IsSwiftResult_NullSpec_ReturnsFalse()
    {
        Assert.False(MarshallingHelpers.IsSwiftResult(null));
    }

    #endregion
}
