// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

// ═══════════════════════════════════════════════════════════════════════
// BX1: Projection Completeness Tests
// ═══════════════════════════════════════════════════════════════════════

#region Sub-task 1: Framework Dependency Type Resolution

public class FrameworkDependencyAbiPathTests
{
    [Fact]
    public void FrameworkDependencyInfo_StoresAbiJsonPath()
    {
        var info = new FrameworkDependencyInfo
        {
            XCFrameworkPath = "/path/to/Dep.xcframework",
            ModuleName = "Dep",
            AbiJsonPath = "/path/to/abi.json",
            TbdPath = "/path/to/file.tbd"
        };

        Assert.Equal("/path/to/abi.json", info.AbiJsonPath);
        Assert.Equal("/path/to/file.tbd", info.TbdPath);
    }

    [Fact]
    public void FrameworkDependencyInfo_NullAbiJsonPathByDefault()
    {
        var info = new FrameworkDependencyInfo
        {
            XCFrameworkPath = "/path/to/Dep.xcframework",
            ModuleName = "Dep"
        };

        Assert.Null(info.AbiJsonPath);
        Assert.Null(info.TbdPath);
    }

    [Fact]
    public void FrameworkDependencyInfo_IsAutoDetectedDefault_IsFalse()
    {
        var info = new FrameworkDependencyInfo
        {
            XCFrameworkPath = "/path/to/Dep.xcframework",
            ModuleName = "Dep"
        };

        Assert.False(info.IsAutoDetected);
    }

    [Fact]
    public void FrameworkDependencyInfo_IsAutoDetected_CanBeSetTrue()
    {
        var info = new FrameworkDependencyInfo
        {
            XCFrameworkPath = "/path/to/Dep.xcframework",
            ModuleName = "Dep",
            IsAutoDetected = true
        };

        Assert.True(info.IsAutoDetected);
    }
}

#endregion

#region Sub-task 2: SwiftOptional Fallback for Unknown Apple Reference Types

public class OptionalAppleFallbackTests
{
    private readonly TypeProjectionFactory _factory = new();

    [Fact]
    public void Project_OptionalUnknownAppleType_ProducesObjCBridgedFallback()
    {
        // Optional<CoreBluetooth.CBCentralManager> — CoreBluetooth is a known Apple module
        var inner = new NamedTypeSpec("CoreBluetooth.CBCentralManager");
        var optional = new NamedTypeSpec("Swift.Optional", inner);
        var ctx = CreateContext();

        var projection = _factory.Project(optional, ctx);

        Assert.NotNull(projection);
        Assert.IsType<OptionalProjection>(projection);
        var optProj = (OptionalProjection)projection;
        Assert.IsType<ObjCBridgedProjection>(optProj.InnerProjection);
    }

    [Fact]
    public void Project_OptionalUnknownMapKitType_ProducesObjCBridgedFallback()
    {
        // Optional<MapKit.MKMapView> — MapKit is a known Apple module
        var inner = new NamedTypeSpec("MapKit.MKMapView");
        var optional = new NamedTypeSpec("Swift.Optional", inner);
        var ctx = CreateContext();

        var projection = _factory.Project(optional, ctx);

        Assert.NotNull(projection);
        Assert.IsType<OptionalProjection>(projection);
    }

    [Fact]
    public void Project_OptionalBareUnknown_ReturnsNull()
    {
        // Optional<SomeType> — no module prefix, must return null
        var inner = new NamedTypeSpec("SomeType");
        var optional = new NamedTypeSpec("Swift.Optional", inner);
        var ctx = CreateContext();

        var projection = _factory.Project(optional, ctx);

        Assert.Null(projection);
    }

    [Fact]
    public void Project_OptionalThirdPartyModule_ReturnsNull()
    {
        // Optional<ThirdParty.SomeType> — non-Apple module, must return null
        var inner = new NamedTypeSpec("ThirdParty.SomeType");
        var optional = new NamedTypeSpec("Swift.Optional", inner);
        var ctx = CreateContext();

        var projection = _factory.Project(optional, ctx);

        Assert.Null(projection);
    }

    [Fact]
    public void Project_OptionalKnownType_NoFallback()
    {
        // Optional<Swift.String> — resolved via normal path (StringProjection)
        var inner = new NamedTypeSpec("Swift.String");
        var optional = new NamedTypeSpec("Swift.Optional", inner);
        var ctx = CreateContext();

        var projection = _factory.Project(optional, ctx);

        Assert.NotNull(projection);
        Assert.IsType<OptionalProjection>(projection);
        var optProj = (OptionalProjection)projection;
        Assert.IsType<StringProjection>(optProj.InnerProjection);
    }

    [Fact]
    public void Project_OptionalUnknownGenericAppleType_ReturnsNull()
    {
        // Optional<WebKit.WKWebView<T>> — generic inner type, must return null
        var inner = new NamedTypeSpec("WebKit.WKWebView",
            new NamedTypeSpec("τ_0_0"));
        var optional = new NamedTypeSpec("Swift.Optional", inner);
        var ctx = CreateContext();

        var projection = _factory.Project(optional, ctx);

        Assert.Null(projection);
    }

    [Fact]
    public void IsKnownAppleModule_KnownModules_ReturnsTrue()
    {
        Assert.True(AppleFrameworkRegistry.IsOptionalFallbackModule("CoreBluetooth"));
        Assert.True(AppleFrameworkRegistry.IsOptionalFallbackModule("WebKit"));
        Assert.True(AppleFrameworkRegistry.IsOptionalFallbackModule("MapKit"));
        Assert.True(AppleFrameworkRegistry.IsOptionalFallbackModule("StoreKit"));
        Assert.True(AppleFrameworkRegistry.IsOptionalFallbackModule("Metal"));
    }

    [Fact]
    public void IsKnownAppleModule_UnknownModules_ReturnsFalse()
    {
        Assert.False(AppleFrameworkRegistry.IsOptionalFallbackModule("Nuke"));
        Assert.False(AppleFrameworkRegistry.IsOptionalFallbackModule("Alamofire"));
        Assert.False(AppleFrameworkRegistry.IsOptionalFallbackModule("ThirdParty"));
        Assert.False(AppleFrameworkRegistry.IsOptionalFallbackModule("Swift"));
        // UIKit and Foundation are now in the set — the IsNestedType guard
        // prevents misprojection of nested types like NSAttributedString.Key.
        Assert.True(AppleFrameworkRegistry.IsOptionalFallbackModule("UIKit"));
        Assert.True(AppleFrameworkRegistry.IsOptionalFallbackModule("Foundation"));
    }

    [Fact]
    public void Project_OptionalSwiftValueTypeInAppleModule_ReturnsNull()
    {
        // Optional<StoreKit.Transaction> — StoreKit is a known Apple module,
        // but "Transaction" lacks ObjC prefix → should NOT get fallback
        var inner = new NamedTypeSpec("StoreKit.Transaction");
        var optional = new NamedTypeSpec("Swift.Optional", inner);
        var ctx = CreateContext();

        var projection = _factory.Project(optional, ctx);

        Assert.Null(projection);
    }

    [Fact]
    public void Project_OptionalSwiftValueTypeInVision_ReturnsNull()
    {
        // Optional<Vision.RecognizedText> — "RecognizedText" lacks ObjC prefix
        var inner = new NamedTypeSpec("Vision.RecognizedText");
        var optional = new NamedTypeSpec("Swift.Optional", inner);
        var ctx = CreateContext();

        var projection = _factory.Project(optional, ctx);

        Assert.Null(projection);
    }

    [Fact]
    public void Project_OptionalUIKitObjCClass_ReturnsOptionalProjection()
    {
        // CQ-7: UIKit ObjC classes like UIFont should project as nullable (UIKit.UIFont?)
        // not as SwiftOptional<UIKit.UIFont>.
        var inner = new NamedTypeSpec("UIKit.UIFont");
        var optional = new NamedTypeSpec("Swift.Optional", inner);
        var ctx = CreateContext();

        var projection = _factory.Project(optional, ctx);

        Assert.NotNull(projection);
        Assert.Equal("UIKit.UIFont?", projection!.PublicType);
    }

    [Fact]
    public void Project_OptionalFoundationObjCClass_ReturnsOptionalProjection()
    {
        var inner = new NamedTypeSpec("Foundation.NSAttributedString");
        var optional = new NamedTypeSpec("Swift.Optional", inner);
        var ctx = CreateContext();

        var projection = _factory.Project(optional, ctx);

        Assert.NotNull(projection);
        Assert.Equal("Foundation.NSAttributedString?", projection!.PublicType);
    }

    [Fact]
    public void Project_OptionalNestedUIKitType_ReturnsNull()
    {
        // Nested types like UIControl.State are structs/enums, not ObjC classes.
        // The IsNestedType guard prevents misprojection.
        var inner = new NamedTypeSpec("UIKit.UIControl.State");
        var optional = new NamedTypeSpec("Swift.Optional", inner);
        var ctx = CreateContext();

        var projection = _factory.Project(optional, ctx);

        Assert.Null(projection);
    }

    [Fact]
    public void HasObjCClassPrefix_ObjCClasses_ReturnsTrue()
    {
        Assert.True(AppleFrameworkRegistry.HasObjCClassPrefix("CoreBluetooth.CBCentralManager"));
        Assert.True(AppleFrameworkRegistry.HasObjCClassPrefix("MapKit.MKMapView"));
        Assert.True(AppleFrameworkRegistry.HasObjCClassPrefix("WebKit.WKWebView"));
        Assert.True(AppleFrameworkRegistry.HasObjCClassPrefix("UIKit.UIViewController"));
        Assert.True(AppleFrameworkRegistry.HasObjCClassPrefix("Vision.VNRequest"));
    }

    [Fact]
    public void HasObjCClassPrefix_SwiftValueTypes_ReturnsFalse()
    {
        Assert.False(AppleFrameworkRegistry.HasObjCClassPrefix("StoreKit.Transaction"));
        Assert.False(AppleFrameworkRegistry.HasObjCClassPrefix("StoreKit.Product"));
        Assert.False(AppleFrameworkRegistry.HasObjCClassPrefix("Vision.RecognizedText"));
        Assert.False(AppleFrameworkRegistry.HasObjCClassPrefix("CoreLocation.Coordinate"));
        Assert.False(AppleFrameworkRegistry.HasObjCClassPrefix("SomeType")); // no module
    }

    private static ProjectionContext CreateContext(ITypeDatabase? db = null)
    {
        return new ProjectionContext
        {
            TypeDatabase = db ?? new MinimalMockTypeDatabase()
        };
    }

    private class MinimalMockTypeDatabase : ITypeDatabase
    {
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            record = null;
            return false;
        }
        public string GetLibraryPath(string moduleName) => "";
        public string? AsyncLibraryName => null;
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #region CQ-5: Array<ObjCClass> Projection Tests

    [Fact]
    public void Project_ArrayOfObjCClass_ReturnsArrayProjection()
    {
        // Array<UIKit.UIImage> — UIKit is a known Apple module, UIImage has ObjC prefix
        var element = new NamedTypeSpec("UIKit.UIImage");
        var array = new NamedTypeSpec("Swift.Array", element);
        var ctx = CreateContext();

        var projection = _factory.Project(array, ctx);

        Assert.NotNull(projection);
        Assert.Contains("IReadOnlyList", projection!.PublicType);
    }

    [Fact]
    public void Project_ArrayOfNonObjCAppleType_ReturnsNull()
    {
        // Array<StoreKit.Transaction> — StoreKit is Apple but Transaction lacks ObjC prefix
        var element = new NamedTypeSpec("StoreKit.Transaction");
        var array = new NamedTypeSpec("Swift.Array", element);
        var ctx = CreateContext();

        var projection = _factory.Project(array, ctx);

        Assert.Null(projection);
    }

    [Fact]
    public void Project_ArrayOfThirdPartyType_ReturnsNull()
    {
        // Array<Nuke.ImagePipeline> — Nuke is not an Apple module
        var element = new NamedTypeSpec("Nuke.ImagePipeline");
        var array = new NamedTypeSpec("Swift.Array", element);
        var ctx = CreateContext();

        var projection = _factory.Project(array, ctx);

        Assert.Null(projection);
    }

    [Fact]
    public void Project_ArrayOfNestedObjCType_ReturnsNull()
    {
        // Array<Foundation.NSAttributedString.Key> — nested type, not an ObjC class
        var element = new NamedTypeSpec("Foundation.NSAttributedString.Key");
        var array = new NamedTypeSpec("Swift.Array", element);
        var ctx = CreateContext();

        var projection = _factory.Project(array, ctx);

        Assert.Null(projection);
    }

    [Fact]
    public void Project_ArrayOfNonUIKitAppleType_ReturnsNull()
    {
        // Array<PassKit.PKPaymentNetwork> — PassKit is Apple but not UIKit/Foundation
        var element = new NamedTypeSpec("PassKit.PKPaymentNetwork");
        var array = new NamedTypeSpec("Swift.Array", element);
        var ctx = CreateContext();

        var projection = _factory.Project(array, ctx);

        Assert.Null(projection);
    }

    [Fact]
    public void Project_SetOfObjCClass_ReturnsSetProjection()
    {
        // Set<UIKit.UIImage> — UIKit ObjC class in Set
        var element = new NamedTypeSpec("UIKit.UIImage");
        var set = new NamedTypeSpec("Swift.Set", element);
        var ctx = CreateContext();

        var projection = _factory.Project(set, ctx);

        Assert.NotNull(projection);
        Assert.Contains("IReadOnlySet", projection!.PublicType);
    }

    [Fact]
    public void Project_DictionaryWithObjCClassValue_ReturnsDictionaryProjection()
    {
        // Dictionary<Swift.String, UIKit.UIImage> — ObjC class as dictionary value
        var key = new NamedTypeSpec("Swift.String");
        var value = new NamedTypeSpec("UIKit.UIImage");
        var dict = new NamedTypeSpec("Swift.Dictionary", key, value);
        var ctx = CreateContext();

        var projection = _factory.Project(dict, ctx);

        Assert.NotNull(projection);
        Assert.Contains("IReadOnlyDictionary", projection!.PublicType);
    }

    [Fact]
    public void Project_SetOfNestedObjCType_ReturnsNull()
    {
        // Set<Foundation.NSAttributedString.Key> — nested type rejected in Set
        var element = new NamedTypeSpec("Foundation.NSAttributedString.Key");
        var set = new NamedTypeSpec("Swift.Set", element);
        var ctx = CreateContext();

        var projection = _factory.Project(set, ctx);

        Assert.Null(projection);
    }

    [Fact]
    public void Project_DictionaryWithNonUIKitAppleKey_ReturnsNull()
    {
        // Dictionary<PassKit.PKPaymentNetwork, Swift.String> — PassKit excluded from element fallback
        var key = new NamedTypeSpec("PassKit.PKPaymentNetwork");
        var value = new NamedTypeSpec("Swift.String");
        var dict = new NamedTypeSpec("Swift.Dictionary", key, value);
        var ctx = CreateContext();

        var projection = _factory.Project(dict, ctx);

        Assert.Null(projection);
    }

    #endregion
}

#endregion

#region Sub-task 3: Collection Projection in Async Contexts

public class AsyncCollectionProjectionTests
{
    [Fact]
    public void AsyncWrapper_ArrayIntReturn_UsesMarshalFromSwiftWithSwiftArray()
    {
        // Async method returning [Int] should marshal via SwiftArray<nint>, not IReadOnlyList<int>
        var (csOutput, _) = GenerateAsyncMethodWithCollectionReturn("Swift.Array", "Swift.Int");

        // Should use SwiftArray<nint> for MarshalFromSwift (runtime container type)
        Assert.Contains("MarshalFromSwift<SwiftArray<", csOutput);

        // Should NOT use the public type IReadOnlyList for MarshalFromSwift
        Assert.DoesNotContain("MarshalFromSwift<IReadOnlyList", csOutput);

        // Should contain .AsProjected for element conversion
        Assert.Contains(".AsProjected(", csOutput);
    }

    [Fact]
    public void AsyncWrapper_ArrayIntReturn_FreesSwiftAllocatedMemory()
    {
        var (csOutput, _) = GenerateAsyncMethodWithCollectionReturn("Swift.Array", "Swift.Int");

        // Should free Swift-allocated memory in finally block
        Assert.Contains("SBW_Free(resultPtr)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_ArrayIntReturn_SwiftUsesOpaquePointer()
    {
        // Swift side should allocate memory and pass OpaquePointer (complex type path)
        var (_, swiftOutput) = GenerateAsyncMethodWithCollectionReturn("Swift.Array", "Swift.Int");

        // Swift wrapper should use OpaquePointer for callback param
        Assert.Contains("OpaquePointer", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_DictionaryReturn_UsesMarshalFromSwiftWithSwiftDictionary()
    {
        var (csOutput, _) = GenerateAsyncMethodWithCollectionReturn(
            "Swift.Dictionary", "Swift.String", "Swift.Int");

        Assert.Contains("MarshalFromSwift<SwiftDictionary<", csOutput);
        Assert.DoesNotContain("MarshalFromSwift<IReadOnlyDictionary", csOutput);
    }

    [Fact]
    public void AsyncWrapper_SetReturn_UsesMarshalFromSwiftWithSwiftSet()
    {
        var (csOutput, _) = GenerateAsyncMethodWithCollectionReturn("Swift.Set", "Swift.Int");

        Assert.Contains("MarshalFromSwift<SwiftSet<", csOutput);
        Assert.DoesNotContain("MarshalFromSwift<IReadOnlySet", csOutput);
    }

    /// <summary>
    /// Generates C# and Swift output for an async method returning a collection type.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncMethodWithCollectionReturn(
        string containerType, string elementType, string? secondElementType = null)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "Pipeline",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MangledName = "$s10TestModule8PipelineCN",
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
        parentDecl.Properties.Add(new PropertyDecl
        {
            Name = "shared",
            IsStatic = true,
            HasStorage = true,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.Pipeline"),
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        });
        moduleDecl.Types.Add(parentDecl);

        // Build the collection TypeSpec
        TypeSpec returnTypeSpec;
        if (secondElementType != null)
        {
            // Dictionary<K, V>
            returnTypeSpec = new NamedTypeSpec(containerType,
                new NamedTypeSpec(elementType), new NamedTypeSpec(secondElementType));
        }
        else
        {
            returnTypeSpec = new NamedTypeSpec(containerType,
                new NamedTypeSpec(elementType));
        }

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = returnTypeSpec,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "fetchCollection",
            MangledName = "$s10TestModule8PipelineC15fetchCollectionSaySiGyYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        var typeDatabase = new TypeDatabase();
        typeDatabase.LoadModuleDatabaseFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SwiftDatabase.xml")).Wait();
        typeDatabase.LoadModuleDatabaseFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "FoundationDatabase.xml")).Wait();

        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        module.RegisterType(
            parentDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(module);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

}

#endregion
