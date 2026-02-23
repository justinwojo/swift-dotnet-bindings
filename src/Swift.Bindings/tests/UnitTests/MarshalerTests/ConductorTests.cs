// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="Conductor"/> handler selection, priority resolution,
/// and fallback behavior across type, method, property, and module declarations.
/// </summary>
public class ConductorTests
{
    private readonly Conductor _conductor;

    public ConductorTests()
    {
        _conductor = new Conductor(NullLoggerFactory.Instance);
    }

    #region Helper Methods

    private static ModuleDecl CreateModuleDecl(string name = "TestModule")
    {
        var module = new ModuleDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
        };
        module.ModuleDecl = module;
        return module;
    }

    private static StructDecl CreateStructDecl(bool isFrozen, string name = "TestStruct", BaseDecl? parent = null)
    {
        var module = CreateModuleDecl();
        return new StructDecl
        {
            Name = name,
            ParentDecl = parent ?? module,
            ModuleDecl = module,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule0{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            IsFrozen = isFrozen,
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
        };
    }

    private static ClassDecl CreateClassDecl(string name = "TestClass")
    {
        var module = CreateModuleDecl();
        return new ClassDecl
        {
            Name = name,
            ParentDecl = module,
            ModuleDecl = module,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule0{name}C",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
        };
    }

    private static EnumDecl CreateEnumDecl(string name = "TestEnum")
    {
        var module = CreateModuleDecl();
        return new EnumDecl
        {
            Name = name,
            ParentDecl = module,
            ModuleDecl = module,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule0{name}O",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            IsFrozen = true,
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
        };
    }

    private static ProtocolDecl CreateProtocolDecl(string name = "TestProtocol")
    {
        var module = CreateModuleDecl();
        return new ProtocolDecl
        {
            Name = name,
            ParentDecl = module,
            ModuleDecl = module,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule0{name}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
        };
    }

    private static MethodDecl CreateMethodDecl(
        string name = "testMethod",
        bool isConstructor = false,
        bool isAsync = false,
        BaseDecl? parent = null,
        MethodType methodType = MethodType.Instance)
    {
        var module = CreateModuleDecl();
        return new MethodDecl
        {
            Name = name,
            ParentDecl = parent ?? module,
            ModuleDecl = module,
            MangledName = $"$s10TestModule0{name}",
            MethodType = methodType,
            IsConstructor = isConstructor,
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsAsync = isAsync,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
        };
    }

    private static PropertyDecl CreatePropertyDecl(string name = "testProperty")
    {
        var module = CreateModuleDecl();
        return new PropertyDecl
        {
            Name = name,
            ParentDecl = module,
            ModuleDecl = module,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
        };
    }

    #endregion

    #region Type Handler Selection

    [Fact]
    public void TryGetTypeHandler_FrozenStruct_ReturnsFrozenStructHandler()
    {
        var decl = CreateStructDecl(isFrozen: true);

        var result = _conductor.TryGetTypeHandler(decl, out var handler);

        Assert.True(result);
        Assert.NotNull(handler);
        Assert.IsType<FrozenStructHandler>(handler);
    }

    [Fact]
    public void TryGetTypeHandler_NonFrozenStruct_ReturnsNonFrozenStructHandler()
    {
        var decl = CreateStructDecl(isFrozen: false);

        var result = _conductor.TryGetTypeHandler(decl, out var handler);

        Assert.True(result);
        Assert.NotNull(handler);
        Assert.IsType<NonFrozenStructHandler>(handler);
    }

    [Fact]
    public void TryGetTypeHandler_Class_ReturnsClassHandler()
    {
        var decl = CreateClassDecl();

        var result = _conductor.TryGetTypeHandler(decl, out var handler);

        Assert.True(result);
        Assert.NotNull(handler);
        Assert.IsType<ClassHandler>(handler);
    }

    [Fact]
    public void TryGetTypeHandler_Enum_ReturnsEnumHandler()
    {
        var decl = CreateEnumDecl();

        var result = _conductor.TryGetTypeHandler(decl, out var handler);

        Assert.True(result);
        Assert.NotNull(handler);
        Assert.IsType<EnumHandler>(handler);
    }

    [Fact]
    public void TryGetTypeHandler_Protocol_ReturnsProtocolHandler()
    {
        var decl = CreateProtocolDecl();

        var result = _conductor.TryGetTypeHandler(decl, out var handler);

        Assert.True(result);
        Assert.NotNull(handler);
        Assert.IsType<ProtocolHandler>(handler);
    }

    #endregion

    #region Method Handler Selection

    [Fact]
    public void TryGetMethodHandler_StructConstructor_ReturnsConstructorHandler()
    {
        var structDecl = CreateStructDecl(isFrozen: true);
        var methodDecl = CreateMethodDecl(
            name: "init",
            isConstructor: true,
            parent: structDecl);

        var result = _conductor.TryGetMethodHandler(methodDecl, out var handler);

        Assert.True(result);
        Assert.NotNull(handler);
        Assert.IsType<ConstructorHandler>(handler);
    }

    [Fact]
    public void TryGetMethodHandler_ClassConstructor_ReturnsConstructorHandler()
    {
        // ConstructorHandlerFactory handles constructors on both StructDecl and ClassDecl,
        // so a class constructor is dispatched to ConstructorHandler for proper C# constructor emission.
        var classDecl = CreateClassDecl();
        var methodDecl = CreateMethodDecl(
            name: "init",
            isConstructor: true,
            parent: classDecl);

        var result = _conductor.TryGetMethodHandler(methodDecl, out var handler);

        Assert.True(result);
        Assert.NotNull(handler);
        Assert.IsType<ConstructorHandler>(handler);
    }

    [Fact]
    public void TryGetMethodHandler_AsyncClassConstructor_ReturnsMethodHandler()
    {
        // Async constructors need callback-based factory method emission (C# doesn't support
        // async constructors), so they fall through to MethodHandler instead of ConstructorHandler.
        var classDecl = CreateClassDecl();
        var methodDecl = CreateMethodDecl(
            name: "init",
            isConstructor: true,
            isAsync: true,
            parent: classDecl);

        var result = _conductor.TryGetMethodHandler(methodDecl, out var handler);

        Assert.True(result);
        Assert.NotNull(handler);
        Assert.IsType<MethodHandler>(handler);
    }

    [Fact]
    public void TryGetMethodHandler_InstanceMethod_ReturnsMethodHandler()
    {
        var methodDecl = CreateMethodDecl(name: "doSomething");

        var result = _conductor.TryGetMethodHandler(methodDecl, out var handler);

        Assert.True(result);
        Assert.NotNull(handler);
        Assert.IsType<MethodHandler>(handler);
    }

    [Fact]
    public void TryGetMethodHandler_StaticMethod_ReturnsMethodHandler()
    {
        var methodDecl = CreateMethodDecl(
            name: "staticMethod",
            methodType: MethodType.Static);

        var result = _conductor.TryGetMethodHandler(methodDecl, out var handler);

        Assert.True(result);
        Assert.NotNull(handler);
        Assert.IsType<MethodHandler>(handler);
    }

    #endregion

    #region Property Handler Selection

    [Fact]
    public void TryGetPropertyHandler_Property_ReturnsPropertyHandler()
    {
        var decl = CreatePropertyDecl();

        var result = _conductor.TryGetPropertyHandler(decl, out var handler);

        Assert.True(result);
        Assert.NotNull(handler);
        Assert.IsType<PropertyHandler>(handler);
    }

    [Fact]
    public void TryGetPropertyHandler_StaticProperty_ReturnsPropertyHandler()
    {
        var decl = CreatePropertyDecl(name: "shared");
        decl.IsStatic = true;

        var result = _conductor.TryGetPropertyHandler(decl, out var handler);

        Assert.True(result);
        Assert.NotNull(handler);
    }

    #endregion

    #region Module Handler Selection

    [Fact]
    public void TryGetModuleHandler_Module_ReturnsModuleHandler()
    {
        var decl = CreateModuleDecl();

        var result = _conductor.TryGetModuleHandler(decl, out var handler);

        Assert.True(result);
        Assert.NotNull(handler);
        Assert.IsType<ModuleHandler>(handler);
    }

    #endregion

    #region Fallback / No Handler Cases

    [Fact]
    public void TryGetArgumentHandler_AnyArgument_ReturnsFalse()
    {
        // Argument handler factory list is empty — no handler should be found.
        var module = CreateModuleDecl();
        var argDecl = new ArgumentDecl
        {
            Name = "arg",
            ParentDecl = module,
            ModuleDecl = module,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            PrivateName = "arg",
            IsInOut = false,
            IsGeneric = false,
        };

        var result = _conductor.TryGetArgumentHandler(argDecl, out var handler);

        Assert.False(result);
        Assert.Null(handler);
    }

    #endregion

    #region Priority Resolution

    [Fact]
    public void TryGetTypeHandler_FrozenVsNonFrozen_CorrectlyDisambiguates()
    {
        // Verify that frozen and non-frozen structs get different handlers
        var frozen = CreateStructDecl(isFrozen: true, name: "FrozenPoint");
        var nonFrozen = CreateStructDecl(isFrozen: false, name: "NonFrozenData");

        _conductor.TryGetTypeHandler(frozen, out var frozenHandler);
        _conductor.TryGetTypeHandler(nonFrozen, out var nonFrozenHandler);

        Assert.IsType<FrozenStructHandler>(frozenHandler);
        Assert.IsType<NonFrozenStructHandler>(nonFrozenHandler);
    }

    [Fact]
    public void TryGetMethodHandler_ConstructorOnStruct_PrioritizedOverGenericMethod()
    {
        // ConstructorHandlerFactory is listed before MethodHandlerFactory,
        // so a struct constructor should match ConstructorHandler first.
        var structDecl = CreateStructDecl(isFrozen: false);
        var ctorDecl = CreateMethodDecl(
            name: "init",
            isConstructor: true,
            parent: structDecl);

        var result = _conductor.TryGetMethodHandler(ctorDecl, out var handler);

        Assert.True(result);
        Assert.IsType<ConstructorHandler>(handler);
    }

    [Fact]
    public void TryGetMethodHandler_NonConstructorOnStruct_ReturnsMethodHandler()
    {
        // A regular instance method on a struct should skip ConstructorHandler
        // and match MethodHandler.
        var structDecl = CreateStructDecl(isFrozen: true);
        var methodDecl = CreateMethodDecl(
            name: "doSomething",
            isConstructor: false,
            parent: structDecl);

        var result = _conductor.TryGetMethodHandler(methodDecl, out var handler);

        Assert.True(result);
        Assert.IsType<MethodHandler>(handler);
    }

    [Fact]
    public void TryGetMethodHandler_ConstructorOnEnum_ReturnsMethodHandler()
    {
        // EnumDecl is not StructDecl, so constructor on enum skips ConstructorHandler.
        var enumDecl = CreateEnumDecl();
        var ctorDecl = CreateMethodDecl(
            name: "init",
            isConstructor: true,
            parent: enumDecl);

        var result = _conductor.TryGetMethodHandler(ctorDecl, out var handler);

        Assert.True(result);
        Assert.IsType<MethodHandler>(handler);
    }

    #endregion

    #region TypeHandlerContext

    [Fact]
    public void TypeHandlerContext_Empty_HasNullProperties()
    {
        var ctx = TypeHandlerContext.Empty;
        Assert.Null(ctx.PInvokeHelperContext);
        Assert.NotNull(ctx.DeferredPInvokeHelperContexts);
        Assert.Empty(ctx.DeferredPInvokeHelperContexts);
        Assert.Null(ctx.PropertyRenames);
    }

    [Fact]
    public void TypeHandlerContext_WithPInvokeHelperContext_CarriesContext()
    {
        var pinvokeCtx = new PInvokeHelperContext("TestType", new List<string> { "T" });
        var ctx = new TypeHandlerContext(pinvokeCtx, new(), null);

        Assert.NotNull(ctx.PInvokeHelperContext);
        Assert.Equal("TestType_PInvoke", ctx.PInvokeHelperContext!.HelperClassName);
    }

    [Fact]
    public void TypeHandlerContext_WithDeferredContexts_CarriesList()
    {
        var deferred = new List<PInvokeHelperContext>
        {
            new PInvokeHelperContext("Inner", new[] { "U0" })
        };
        var ctx = new TypeHandlerContext(null, deferred, null);

        Assert.Single(ctx.DeferredPInvokeHelperContexts);
        Assert.Equal("Inner_PInvoke", ctx.DeferredPInvokeHelperContexts[0].HelperClassName);
    }

    #endregion

    #region Multiple Calls Return Fresh Handlers

    [Fact]
    public void TryGetTypeHandler_MultipleCalls_ReturnDifferentInstances()
    {
        var decl = CreateClassDecl();

        _conductor.TryGetTypeHandler(decl, out var handler1);
        _conductor.TryGetTypeHandler(decl, out var handler2);

        Assert.NotNull(handler1);
        Assert.NotNull(handler2);
        // Construct() creates new instances each time
        Assert.NotSame(handler1, handler2);
    }

    #endregion
}
