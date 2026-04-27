// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests that ModuleProcessor resolves class hierarchies correctly.
/// </summary>
public class ClassHierarchyResolutionTests
{
    [Fact]
    public void ResolveClassHierarchy_ThreeLevelChain_ResolvesAllLinks()
    {
        // Alamofire pattern: UploadRequest → DataRequest → Request
        var request = CreateClassDecl("Request", "TestModule");
        var dataRequest = CreateClassDecl("DataRequest", "TestModule",
            superclassNames: new[] { "TestModule.Request" });
        var uploadRequest = CreateClassDecl("UploadRequest", "TestModule",
            superclassNames: new[] { "TestModule.DataRequest", "TestModule.Request" });

        RunResolution(request, dataRequest, uploadRequest);

        Assert.Same(dataRequest, uploadRequest.ResolvedSuperclass);
        Assert.Same(request, dataRequest.ResolvedSuperclass);
        Assert.Null(request.ResolvedSuperclass);
        Assert.True(uploadRequest.HasResolvedSuperclass);
        Assert.True(dataRequest.HasResolvedSuperclass);
        Assert.False(request.HasResolvedSuperclass);
    }

    [Fact]
    public void ResolveClassHierarchy_ObjCBase_LeavesUnresolved()
    {
        var cls = CreateClassDecl("SessionDelegate", "TestModule",
            superclassUsr: "c:objc(cs)NSObject",
            superclassNames: new[] { "ObjectiveC.NSObject" });

        RunResolution(cls);

        Assert.Null(cls.ResolvedSuperclass);
        Assert.True(cls.HasExternalSuperclass);
        Assert.False(cls.HasResolvedSuperclass);
    }

    [Fact]
    public void ResolveClassHierarchy_CrossModuleBase_LeavesUnresolved()
    {
        var cls = CreateClassDecl("MyClass", "ModuleA",
            superclassNames: new[] { "ModuleB.SomeClass" });

        RunResolution(cls);

        Assert.Null(cls.ResolvedSuperclass);
        Assert.True(cls.HasExternalSuperclass);
    }

    [Fact]
    public void ResolveClassHierarchy_RootClass_HasNoSuperclass()
    {
        var cls = CreateClassDecl("RootClass", "TestModule");

        RunResolution(cls);

        Assert.Null(cls.ResolvedSuperclass);
        Assert.Null(cls.DirectSuperclassName);
        Assert.False(cls.HasResolvedSuperclass);
        Assert.False(cls.HasExternalSuperclass);
    }

    [Fact]
    public void ResolveClassHierarchy_MultipleIndependentHierarchies_ResolvesCorrectly()
    {
        // Two independent hierarchies in the same module
        var animalBase = CreateClassDecl("Animal", "TestModule");
        var dog = CreateClassDecl("Dog", "TestModule",
            superclassNames: new[] { "TestModule.Animal" });

        var vehicleBase = CreateClassDecl("Vehicle", "TestModule");
        var car = CreateClassDecl("Car", "TestModule",
            superclassNames: new[] { "TestModule.Vehicle" });

        RunResolution(animalBase, dog, vehicleBase, car);

        Assert.Same(animalBase, dog.ResolvedSuperclass);
        Assert.Same(vehicleBase, car.ResolvedSuperclass);
        Assert.Null(animalBase.ResolvedSuperclass);
        Assert.Null(vehicleBase.ResolvedSuperclass);
    }

    [Fact]
    public void ResolveClassHierarchy_SameModuleBase_Resolves()
    {
        var baseClass = CreateClassDecl("Base", "TestModule");
        var derived = CreateClassDecl("Derived", "TestModule",
            superclassNames: new[] { "TestModule.Base" });

        RunResolution(baseClass, derived);

        Assert.Same(baseClass, derived.ResolvedSuperclass);
        Assert.True(derived.HasResolvedSuperclass);
        Assert.False(derived.HasExternalSuperclass);
    }

    [Fact]
    public void ResolveClassHierarchy_UmbrellaModuleMismatch_ResolvesViaUsr()
    {
        // RealityFoundation pattern: ABI says superclassNames = ["RealityKit.Entity"]
        // (the original Swift module) but the parser keys both classes under the
        // umbrella module "RealityFoundation". The USR is canonical and contains
        // the umbrella module, so it must be the source of truth for resolution.
        var entity = CreateClassDecl("Entity", "RealityFoundation");
        var modelEntity = CreateClassDecl("ModelEntity", "RealityFoundation",
            superclassUsr: "s:17RealityFoundation6EntityC",
            superclassNames: new[] { "RealityKit.Entity" });

        RunResolution(entity, modelEntity);

        Assert.Same(entity, modelEntity.ResolvedSuperclass);
        Assert.True(modelEntity.HasResolvedSuperclass);
        Assert.False(modelEntity.HasExternalSuperclass);
    }

    [Fact]
    public void ResolveClassHierarchy_GenericInstantiatedParent_DoesNotResolveViaUsr()
    {
        // RxSwift pattern: class HistoricalScheduler : VirtualTimeScheduler<HistoricalSchedulerTimeConverter>
        // Same-module parent, but the inheritance includes a concrete generic argument the USR
        // does NOT carry. Resolving via USR would silently drop the type argument and emit
        // VirtualTimeScheduler<TConverter> instead of the concrete instantiation, causing CS0246.
        var parent = CreateClassDecl("VirtualTimeScheduler", "RxSwift");
        var child = CreateClassDecl("HistoricalScheduler", "RxSwift",
            superclassUsr: "s:7RxSwift22VirtualTimeSchedulerC",
            superclassNames: new[] { "RxSwift.VirtualTimeScheduler<RxSwift.HistoricalSchedulerTimeConverter>" });

        RunResolution(parent, child);

        // Must NOT resolve — same-module emission requires generic-arg fidelity that the USR lacks.
        Assert.Null(child.ResolvedSuperclass);
        Assert.True(child.HasExternalSuperclass);
    }

    [Theory]
    [InlineData("s:17RealityFoundation6EntityC", "RealityFoundation.Entity")]
    [InlineData("s:9Alamofire11DataRequestC", "Alamofire.DataRequest")]
    [InlineData("s:5MyMod5OuterC5InnerC", "MyMod.Outer.Inner")]
    public void TryParseSwiftClassUsr_ValidClassUsr_ParsesToCanonicalName(string usr, string expected)
    {
        var ok = ModuleProcessor.TryParseSwiftClassUsr(usr, out var name);
        Assert.True(ok);
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("c:objc(cs)NSObject")]                       // ObjC USR
    [InlineData("s:17RealityFoundation6EntityV")]            // V = struct, not class
    [InlineData("s:Foo")]                                    // No length-prefixed segments
    [InlineData("")]                                         // Empty
    [InlineData("s:")]                                       // Prefix only
    public void TryParseSwiftClassUsr_NonClassOrInvalidUsr_ReturnsFalse(string usr)
    {
        var ok = ModuleProcessor.TryParseSwiftClassUsr(usr, out var name);
        Assert.False(ok);
        Assert.Null(name);
    }

    #region Test Helpers

    private static ClassDecl CreateClassDecl(
        string name,
        string moduleName,
        string? superclassUsr = null,
        string[]? superclassNames = null)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            SuperclassUsr = superclassUsr,
            SuperclassNames = superclassNames?.ToList() ?? new List<string>(),
        };
    }

    /// <summary>
    /// Runs ModuleProcessor.FinalizeTypeProcessingAndCreateModuleDatabase which includes
    /// ResolveClassHierarchy. The processor uses a dummy dylib path — ProcessClass will
    /// log a warning but still register the class and resolve hierarchy.
    /// </summary>
    private static void RunResolution(params ClassDecl[] classDecls)
    {
        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>();
        foreach (var cls in classDecls)
        {
            var typeSpec = new NamedTypeSpec(cls.SwiftTypeName.ModuleQualifiedName);
            typeDecls[typeSpec] = cls;
        }

        var typeDatabase = new TypeDatabase();
        var processor = new ModuleProcessor(
            classDecls[0].SwiftTypeName.Module,
            "/tmp/dummy.dylib",
            "TestModule",
            typeDecls,
            typeDatabase,
            NullLogger.Instance);

        processor.FinalizeTypeProcessingAndCreateModuleDatabase();
    }

    #endregion
}
