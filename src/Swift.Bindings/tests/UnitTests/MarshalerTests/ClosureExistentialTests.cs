// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regression tests for WU2: Closure delegate types use protocol interfaces instead of ExistentialContainer.
/// Verifies ClosureHandler.TranslateTypeSpecToCSharp, TranslateTypeSpecToPInvokeType, and NeedsProxyWrapping.
/// </summary>
public class ClosureExistentialTests
{
    #region TranslateTypeSpecToCSharp — existential params in closures

    [Fact]
    public void TranslateTypeSpecToCSharp_ExistentialParam_KnownProtocol_ReturnsInterface()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing");
        var handler = new ClosureHandler(typeDatabase);

        // any ImageProcessing → should return IImageProcessing (not ExistentialContainer1)
        var existentialSpec = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var result = handler.TranslateTypeSpecToCSharp(existentialSpec);

        Assert.Equal("IImageProcessing", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_ExistentialParam_UnknownProtocol_ReturnsContainer()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // any UnknownProtocol → no TypeRecord → falls back to ExistentialContainer1
        var existentialSpec = new NamedTypeSpec("TestModule.UnknownProtocol") { IsAny = true };
        var result = handler.TranslateTypeSpecToCSharp(existentialSpec);

        Assert.Contains("ExistentialContainer", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_ExistentialParam_ObjectFallback_ReturnsContainer()
    {
        // A composition where ObjC filtering removes all non-ObjC protocols → "object" fallback.
        // Use UIKit.UIViewControllerTransitioningDelegate (in AppleObjCFrameworkModules)
        // with TypeRecord Kind=Protocol but no non-ObjC protocol remains after filtering.
        var typeDatabase = CreateTypeDatabaseWithAppleFrameworkProtocol();
        var handler = new ClosureHandler(typeDatabase);

        // Single protocol from Apple framework module: GetPublicExistentialType returns
        // the interface name, but TryGetFilteredProxyClassName filters it → container fallback.
        var existentialSpec = new NamedTypeSpec("UIKit.UIViewControllerTransitioningDelegate") { IsAny = true };
        var result = handler.TranslateTypeSpecToCSharp(existentialSpec);

        Assert.Contains("ExistentialContainer", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_MultiProtocolExistential_ReturnsCompositionInterface()
    {
        var typeDatabase = CreateTypeDatabaseWithTwoProtocols(
            "TestModule.ImageProcessing", "TestModule.DataCaching");
        var handler = new ClosureHandler(typeDatabase);

        // any ImageProcessing & DataCaching → composition interface
        var protocol1 = new NamedTypeSpec("TestModule.ImageProcessing");
        var protocol2 = new NamedTypeSpec("TestModule.DataCaching");
        var protocolList = new ProtocolListTypeSpec(new[] { protocol1, protocol2 });

        var result = handler.TranslateTypeSpecToCSharp(protocolList);

        // Should be a composition interface name, not ExistentialContainer2
        Assert.DoesNotContain("ExistentialContainer", result);
        Assert.StartsWith("I", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_OptionalExistential_StillUsesContainer()
    {
        // Optional<any Protocol> in closure params must keep SwiftOptional<ExistentialContainer1>
        // because the void* path uses MarshalFromSwift which doesn't support interface types.
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing");
        var handler = new ClosureHandler(typeDatabase);

        var existentialInner = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(existentialInner);

        var result = handler.TranslateTypeSpecToCSharp(optionalSpec);

        // Should contain ExistentialContainer (via the bound generic path, line 858-863)
        Assert.Contains("ExistentialContainer", result);
    }

    [Fact]
    public void TranslateTypeSpecToPInvokeType_ExistentialParam_StillReturnsContainer()
    {
        // P/Invoke type must always be the blittable ExistentialContainer, never the interface
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing");
        var handler = new ClosureHandler(typeDatabase);

        var existentialSpec = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var result = handler.TranslateTypeSpecToPInvokeType(existentialSpec);

        Assert.Contains("ExistentialContainer", result);
    }

    [Fact]
    public void BoundGenericParam_Existential_StillUsesContainer()
    {
        // Existential inside a bound generic (TranslateBoundGenericToCSharp line 863)
        // should still use ExistentialContainer, not the interface
        var typeDatabase = CreateTypeDatabaseWithProtocolAndOptional("TestModule.ImageProcessing");
        var handler = new ClosureHandler(typeDatabase);

        var existentialInner = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(existentialInner);

        var result = handler.TranslateTypeSpecToCSharp(optionalSpec);

        // The bound generic path at line 858-863 uses GetCSharpExistentialType (not public)
        Assert.Contains("ExistentialContainer", result);
    }

    #endregion

    #region NeedsProxyWrapping

    [Fact]
    public void NeedsProxyWrapping_KnownProtocol_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing");
        var handler = new ClosureHandler(typeDatabase);

        var existentialSpec = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var result = handler.NeedsProxyWrapping(existentialSpec, out var proxyName);

        Assert.True(result);
        Assert.Equal("ImageProcessingProxy", proxyName);
    }

    [Fact]
    public void NeedsProxyWrapping_UnknownProtocol_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var existentialSpec = new NamedTypeSpec("TestModule.UnknownProtocol") { IsAny = true };
        var result = handler.NeedsProxyWrapping(existentialSpec, out _);

        Assert.False(result);
    }

    [Fact]
    public void NeedsProxyWrapping_AppleFrameworkProtocol_ReturnsFalse()
    {
        // UIKit protocol: IsObjCModuleType returns true → TryGetFilteredProxyClassName filters it
        var typeDatabase = CreateTypeDatabaseWithAppleFrameworkProtocol();
        var handler = new ClosureHandler(typeDatabase);

        var existentialSpec = new NamedTypeSpec("UIKit.UIViewControllerTransitioningDelegate") { IsAny = true };
        var result = handler.NeedsProxyWrapping(existentialSpec, out _);

        Assert.False(result);
    }

    [Fact]
    public void NeedsProxyWrapping_NonExistential_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var nonExistentialSpec = new NamedTypeSpec("Swift.Int");
        var result = handler.NeedsProxyWrapping(nonExistentialSpec, out _);

        Assert.False(result);
    }

    [Fact]
    public void NeedsProxyWrapping_MixedComposition_ReturnsFalse()
    {
        // P1 fix: any ImageProcessing & UIViewControllerTransitioningDelegate
        // ObjC filtering drops UIKit protocol → PProxy takes ExistentialContainer1
        // but P/Invoke passes ExistentialContainer2 (2 witness tables) → mismatch.
        // NeedsProxyWrapping must return false to avoid invalid proxy constructor call.
        var typeDatabase = CreateTypeDatabaseWithMixedComposition();
        var handler = new ClosureHandler(typeDatabase);

        var protocol1 = new NamedTypeSpec("TestModule.ImageProcessing");
        var protocol2 = new NamedTypeSpec("UIKit.UIViewControllerTransitioningDelegate");
        var protocolList = new ProtocolListTypeSpec(new[] { protocol1, protocol2 });

        var result = handler.NeedsProxyWrapping(protocolList, out _);

        Assert.False(result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_MixedComposition_ReturnsContainer()
    {
        // P1 fix: Mixed ObjC + non-ObjC composition in closure params
        // should keep ExistentialContainer (not collapse to single-protocol interface).
        var typeDatabase = CreateTypeDatabaseWithMixedComposition();
        var handler = new ClosureHandler(typeDatabase);

        var protocol1 = new NamedTypeSpec("TestModule.ImageProcessing");
        var protocol2 = new NamedTypeSpec("UIKit.UIViewControllerTransitioningDelegate");
        var protocolList = new ProtocolListTypeSpec(new[] { protocol1, protocol2 });

        var result = handler.TranslateTypeSpecToCSharp(protocolList);

        Assert.Contains("ExistentialContainer", result);
    }

    #endregion

    #region Helpers

    private static ModuleTypeDatabase CreateSwiftModule()
    {
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        return swiftModule;
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddModuleDatabase(CreateSwiftModule());
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocol(string protocolName)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = CreateSwiftModule();
        typeDatabase.AddModuleDatabase(swiftModule);

        var parts = protocolName.Split('.');
        var moduleName = parts[0];
        var shortName = parts[1];

        var testModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(protocolName),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, $"I{shortName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithTwoProtocols(string protocol1Name, string protocol2Name)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = CreateSwiftModule();
        typeDatabase.AddModuleDatabase(swiftModule);

        // Both protocols must be in the same module for this test
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        foreach (var protocolName in new[] { protocol1Name, protocol2Name })
        {
            var parts = protocolName.Split('.');
            var shortName = parts[1];

            testModule.RegisterType(
                SwiftTypeName.FromModuleQualifiedName(protocolName),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", $"I{shortName}"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                    MetadataAccessor = "$sMa",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Protocol
                });
        }
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithAppleFrameworkProtocol()
    {
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddModuleDatabase(CreateSwiftModule());

        // UIKit is in AppleObjCFrameworkModules → IsObjCModuleType returns true
        var uikitModule = new ModuleTypeDatabase("UIKit", "/System/Library/Frameworks/UIKit.framework/UIKit");
        uikitModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("UIKit.UIViewControllerTransitioningDelegate"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "IUIViewControllerTransitioningDelegate"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIViewControllerTransitioningDelegate"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(uikitModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithObjCProtocol()
    {
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddModuleDatabase(CreateSwiftModule());

        var objcModule = new ModuleTypeDatabase("ObjectiveC", "/usr/lib/libobjc.dylib");
        objcModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("ObjectiveC.NSObjectProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ObjectiveC", "INSObjectProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ObjectiveC.NSObjectProtocol"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(objcModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocolAndOptional(string protocolName)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = CreateSwiftModule();
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var parts = protocolName.Split('.');
        var moduleName = parts[0];
        var shortName = parts[1];

        var testModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(protocolName),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, $"I{shortName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithMixedComposition()
    {
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddModuleDatabase(CreateSwiftModule());

        // Non-ObjC protocol
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ImageProcessing"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IImageProcessing"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImageProcessing"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);

        // ObjC protocol (UIKit module → IsObjCModuleType returns true)
        var uikitModule = new ModuleTypeDatabase("UIKit", "/System/Library/Frameworks/UIKit.framework/UIKit");
        uikitModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("UIKit.UIViewControllerTransitioningDelegate"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "IUIViewControllerTransitioningDelegate"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIViewControllerTransitioningDelegate"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(uikitModule);
        return typeDatabase;
    }

    #endregion
}
