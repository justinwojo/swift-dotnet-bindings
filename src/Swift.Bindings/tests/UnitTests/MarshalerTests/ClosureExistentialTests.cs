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
    public void TranslateTypeSpecToCSharp_ExistentialParam_UnknownProtocol_ReturnsObject()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // any UnknownProtocol → no TypeRecord → falls back to "object"
        var existentialSpec = new NamedTypeSpec("TestModule.UnknownProtocol") { IsAny = true };
        var result = handler.TranslateTypeSpecToCSharp(existentialSpec);

        Assert.Equal("object", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_ExistentialParam_ObjectFallback_ReturnsObject()
    {
        // A composition where ObjC filtering removes all non-ObjC protocols → "object" fallback.
        // Use UIKit.UIViewControllerTransitioningDelegate (in AppleObjCFrameworkModules)
        // with TypeRecord Kind=Protocol but no non-ObjC protocol remains after filtering.
        var typeDatabase = CreateTypeDatabaseWithAppleFrameworkProtocol();
        var handler = new ClosureHandler(typeDatabase);

        // Single protocol from Apple framework module: GetPublicExistentialType returns
        // the interface name, but TryGetFilteredProxyClassName filters it → "object" fallback.
        var existentialSpec = new NamedTypeSpec("UIKit.UIViewControllerTransitioningDelegate") { IsAny = true };
        var result = handler.TranslateTypeSpecToCSharp(existentialSpec);

        Assert.Equal("object", result);
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
    public void TranslateTypeSpecToCSharp_MixedComposition_ReturnsObject()
    {
        // P1 fix: Mixed ObjC + non-ObjC composition in closure params
        // should return "object" (not ExistentialContainer or collapse to single-protocol interface).
        var typeDatabase = CreateTypeDatabaseWithMixedComposition();
        var handler = new ClosureHandler(typeDatabase);

        var protocol1 = new NamedTypeSpec("TestModule.ImageProcessing");
        var protocol2 = new NamedTypeSpec("UIKit.UIViewControllerTransitioningDelegate");
        var protocolList = new ProtocolListTypeSpec(new[] { protocol1, protocol2 });

        var result = handler.TranslateTypeSpecToCSharp(protocolList);

        Assert.Equal("object", result);
    }

    #endregion

    #region Return type existentials (Step 2b)

    [Fact]
    public void TranslateTypeSpecToCSharp_ExistentialReturn_KnownProtocol_ReturnsInterface()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing");
        var handler = new ClosureHandler(typeDatabase);

        // any ImageProcessing as return type → should return IImageProcessing (not ExistentialContainer1)
        var existentialSpec = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var result = handler.TranslateTypeSpecToCSharp(existentialSpec, isReturnType: true);

        Assert.Equal("IImageProcessing", result);
    }

    [Fact]
    public void TranslateTypeSpecToCSharp_ExistentialReturn_UnknownProtocol_ReturnsObject()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // any UnknownProtocol as return type → "object"
        var existentialSpec = new NamedTypeSpec("TestModule.UnknownProtocol") { IsAny = true };
        var result = handler.TranslateTypeSpecToCSharp(existentialSpec, isReturnType: true);

        Assert.Equal("object", result);
    }

    #endregion

    #region Bound generic existentials (Step 2d)

    [Fact]
    public void BoundGenericParam_Existential_KnownProtocol_ReturnsInterface()
    {
        // SwiftArray<any ImageProcessing> → SwiftArray<IImageProcessing> (not ExistentialContainer1)
        var typeDatabase = CreateTypeDatabaseWithProtocolAndArray("TestModule.ImageProcessing");
        var handler = new ClosureHandler(typeDatabase);

        var existentialInner = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(existentialInner);

        var result = handler.TranslateTypeSpecToCSharp(arraySpec);

        Assert.Contains("SwiftArray", result);
        Assert.Contains("IImageProcessing", result);
        Assert.DoesNotContain("ExistentialContainer", result);
    }

    [Fact]
    public void BoundGenericParam_Existential_UnknownProtocol_ReturnsObject()
    {
        // SwiftArray<any UnknownProtocol> → SwiftArray<object>
        var typeDatabase = CreateTypeDatabaseWithArray();
        var handler = new ClosureHandler(typeDatabase);

        var existentialInner = new NamedTypeSpec("TestModule.UnknownProtocol") { IsAny = true };
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(existentialInner);

        var result = handler.TranslateTypeSpecToCSharp(arraySpec);

        Assert.Contains("SwiftArray", result);
        Assert.Contains("object", result);
        Assert.DoesNotContain("ExistentialContainer", result);
    }

    #endregion

    #region IsExistentialParam and GetPInvokeExistentialType helpers

    [Fact]
    public void IsExistentialParam_UnknownProtocol_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var existentialSpec = new NamedTypeSpec("TestModule.UnknownProtocol") { IsAny = true };
        Assert.True(handler.IsExistentialParam(existentialSpec));
    }

    [Fact]
    public void IsExistentialParam_KnownProtocol_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing");
        var handler = new ClosureHandler(typeDatabase);

        var existentialSpec = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        Assert.False(handler.IsExistentialParam(existentialSpec));
    }

    [Fact]
    public void IsExistentialParam_WellKnownProtocol_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Swift.Error → AnyError is well-known, not unknown
        var existentialSpec = new NamedTypeSpec("Swift.Error") { IsAny = true };
        Assert.False(handler.IsExistentialParam(existentialSpec));
    }

    [Fact]
    public void IsExistentialParam_NonExistential_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        var intSpec = new NamedTypeSpec("Swift.Int");
        Assert.False(handler.IsExistentialParam(intSpec));
    }

    [Fact]
    public void GetPInvokeExistentialType_SingleProtocol_ReturnsContainer1()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing");
        var handler = new ClosureHandler(typeDatabase);

        var existentialSpec = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var result = handler.GetPInvokeExistentialType(existentialSpec);

        Assert.Equal("Swift.Runtime.ExistentialContainer1", result);
    }

    #endregion

    #region Tuple existential safety gate (Step 2d)

    [Fact]
    public void HasClosureUnsafeTupleElements_WithExistentialElement_ReturnsFalse()
    {
        // Existential in tuple: P/Invoke uses ExistentialContainer1 but C# uses object/IProtocol
        // → emitter now handles per-element conversion, so the gate no longer blocks
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing");
        var tupleHandler = new TupleHandler(typeDatabase);

        var existentialElement = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var intElement = new NamedTypeSpec("Swift.Int");
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec> { existentialElement, intElement });

        var result = tupleHandler.HasClosureUnsafeTupleElements(tupleSpec);

        Assert.False(result);
    }

    [Fact]
    public void HasClosureUnsafeTupleElements_WithPrimitivesOnly_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithDoublePrimitive();
        var tupleHandler = new TupleHandler(typeDatabase);

        var intElement = new NamedTypeSpec("Swift.Int");
        var doubleElement = new NamedTypeSpec("Swift.Double");
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec> { intElement, doubleElement });

        var result = tupleHandler.HasClosureUnsafeTupleElements(tupleSpec);

        Assert.False(result);
    }

    [Fact]
    public void IsSupportedClosure_TupleReturnWithExistential_IsSupported()
    {
        // A closure returning (any Protocol, Int) — the emitter now handles per-element
        // existential conversion in both callback and invoker directions, so this is supported.
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing");
        var handler = new ClosureHandler(typeDatabase);

        var existentialElement = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var intElement = new NamedTypeSpec("Swift.Int");
        var tupleReturn = new TupleTypeSpec(new List<TypeSpec> { existentialElement, intElement });

        // Build closure: () -> (any ImageProcessing, Int)
        var closureSpec = new ClosureTypeSpec(null, tupleReturn);

        // The closure is now supported with per-element existential conversion
        Assert.True(handler.IsSupportedClosure(closureSpec));
    }

    [Fact]
    public void IsSupportedClosure_TupleParamWithExistential_IsSupported()
    {
        // A closure taking (any Protocol, Int) as a parameter — the emitter now handles
        // per-element existential conversion, so tuple params with existentials are supported.
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing");
        var handler = new ClosureHandler(typeDatabase);

        var existentialElement = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var intElement = new NamedTypeSpec("Swift.Int");
        var tupleParam = new TupleTypeSpec(new List<TypeSpec> { existentialElement, intElement });

        // Build closure: ((any ImageProcessing, Int)) -> Void
        var closureSpec = new ClosureTypeSpec(tupleParam, TupleTypeSpec.Empty);

        // The closure is now supported with per-element existential conversion
        Assert.True(handler.IsSupportedClosure(closureSpec));
    }

    #endregion

    #region Proxy-suppression oracle — structurally never-emitted (Self/AT) proxies

    // The flags-half residual for the closure oracle trio: a CONSTRAINED Self/AT existential
    // (`any P<Int>`) projects to the generic interface (`IImageProcessing<nint>`, not `object`),
    // passes NeedsProxyWrapping, and — when the protocol is TypeRecord-only (foreign/dependency,
    // so the suppressed-name precompute never visits it) — is absent from the suppressed-name
    // set. Yet ProtocolProxyEmitter never emits a proxy class for a Self/AT protocol, so every
    // closure site that names `{P}Proxy` for this shape ships a dangling CS0246. The trio must
    // treat the structurally-never-emitted proxy exactly like a name-suppressed one.

    private static readonly TypeSpec ConstrainedExistentialSpec =
        new NamedTypeSpec("TestModule.ImageProcessing", new NamedTypeSpec("Swift.Int")) { IsAny = true };

    [Theory]
    [InlineData(TypeRecordFlags.HasSelfRequirement)]
    [InlineData(TypeRecordFlags.HasAssociatedTypes)]
    public void GetQualifiedProxyClassName_ConstrainedSelfATExistential_ReturnsNull(TypeRecordFlags flag)
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing", flag);
        var handler = new ClosureHandler(typeDatabase);

        // CONSUME: the wrap-fallback name must be withheld so callers emit the no-fallback overload.
        Assert.Null(handler.GetQualifiedProxyClassName(ConstrainedExistentialSpec));
    }

    [Theory]
    [InlineData(TypeRecordFlags.HasSelfRequirement)]
    [InlineData(TypeRecordFlags.HasAssociatedTypes)]
    public void ThrowIfProxyReferenceSuppressed_ConstrainedSelfATExistential_Throws(TypeRecordFlags flag)
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing", flag);
        var handler = new ClosureHandler(typeDatabase);

        // PRODUCE: the invoker construction cannot be degraded in place — the member-emit
        // checkpoint must catch and restub, same as a name-suppressed proxy.
        Assert.Throws<SuppressedProxyReferenceException>(
            () => handler.ThrowIfProxyReferenceSuppressed(ConstrainedExistentialSpec));
    }

    [Theory]
    [InlineData(TypeRecordFlags.HasSelfRequirement)]
    [InlineData(TypeRecordFlags.HasAssociatedTypes)]
    public void IsProxyReferenceSuppressed_ConstrainedSelfATExistential_True(TypeRecordFlags flag)
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing", flag);
        var handler = new ClosureHandler(typeDatabase);

        // UCO trampoline guard: must branch to the safe no-op body up front (a throw across the
        // native boundary would SIGABRT), so the predicate must cover the structural half too.
        Assert.True(handler.IsProxyReferenceSuppressed(ConstrainedExistentialSpec));
    }

    [Fact]
    public void IsProxyReferenceSuppressed_ContainerOfConstrainedSelfATExistential_True()
    {
        // Container recursion: an Array whose ELEMENT is the never-emitted-proxy existential must
        // trip the guard the same way a suppressed-name element does.
        var typeDatabase = CreateTypeDatabaseWithProtocol(
            "TestModule.ImageProcessing", TypeRecordFlags.HasAssociatedTypes);
        var handler = new ClosureHandler(typeDatabase);

        var arraySpec = new NamedTypeSpec("Swift.Array", ConstrainedExistentialSpec);
        Assert.True(handler.IsProxyReferenceSuppressed(arraySpec));
    }

    [Fact]
    public void ClosureOracle_ConstrainedExistential_PlainProtocol_ProxyStaysLive()
    {
        // Green companion: the same constrained shape on a protocol WITHOUT Self/AT flags keeps
        // its live proxy — the broadening must key strictly on the structural flags.
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing");
        var handler = new ClosureHandler(typeDatabase);

        Assert.Equal("ImageProcessingProxy", handler.GetQualifiedProxyClassName(ConstrainedExistentialSpec));
        handler.ThrowIfProxyReferenceSuppressed(ConstrainedExistentialSpec); // must not throw
        Assert.False(handler.IsProxyReferenceSuppressed(ConstrainedExistentialSpec));
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
                CSharpTypeName = CSharpTypeName.NIntType,
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

    private static TypeDatabase CreateTypeDatabaseWithProtocol(
        string protocolName, TypeRecordFlags flags = TypeRecordFlags.None)
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
                Flags = flags,
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

    private static TypeDatabase CreateTypeDatabaseWithDoublePrimitive()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = CreateSwiftModule();
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
                MetadataAccessor = "$sSdMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithArray()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = CreateSwiftModule();
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocolAndArray(string protocolName)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = CreateSwiftModule();
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
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
