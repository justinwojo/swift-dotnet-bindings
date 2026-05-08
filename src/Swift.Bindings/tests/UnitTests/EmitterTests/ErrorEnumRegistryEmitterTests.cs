// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Phase 4 Layer 1 — verifies the per-module error-type registry built by
/// <see cref="ErrorEnumRegistryEmitter"/>. The registry feeds the wire-format
/// extension, Swift cascade helper, and C# typed-exception dispatcher emitted
/// by subsequent layers, so its determinism (alphabetical ordering, idempotent
/// precompute) is load-bearing.
/// </summary>
public class ErrorEnumRegistryEmitterTests
{
    [Fact]
    public void Precompute_EnumConformingToSwiftError_RegisteredWithId1()
    {
        var moduleDecl = BuildModule();
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "WeatherError", "Swift.Error"));

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.WeatherError", out var id));
        Assert.Equal(1, id); // id 0 is reserved for "untyped"
    }

    [Fact]
    public void Precompute_MultipleErrorEnums_AssignedAlphabeticalIds()
    {
        // Source order: Zebra, Apple, Mango. Registered order must be alphabetical
        // for cross-run determinism (the C# Dictionary<int, Type> + Swift cascade
        // emit consume ErrorTypeOrder, which mirrors id assignment).
        var moduleDecl = BuildModule();
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "ZebraError", "Swift.Error"));
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "AppleError", "Swift.Error"));
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "MangoError", "Swift.Error"));

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.AppleError", out var appleId));
        Assert.True(ctx.TryGetErrorTypeId("TestModule.MangoError", out var mangoId));
        Assert.True(ctx.TryGetErrorTypeId("TestModule.ZebraError", out var zebraId));
        Assert.Equal(1, appleId);
        Assert.Equal(2, mangoId);
        Assert.Equal(3, zebraId);
        Assert.Equal(
            new[] { "TestModule.AppleError", "TestModule.MangoError", "TestModule.ZebraError" },
            ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_StructConformingToError_Registered()
    {
        var moduleDecl = BuildModule();
        var structDecl = new StructDecl
        {
            Name = "MyStructError",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyStructError"),
            MangledName = "",
            IsFrozen = false,
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: SwiftTypeName.FromModuleQualifiedName("TestModule.MyStructError"),
                    Protocol: SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                    ProtocolConformanceDescriptor: ""),
            },
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(structDecl);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.MyStructError", out _));
    }

    [Fact]
    public void Precompute_ClassConformingToError_Registered()
    {
        var moduleDecl = BuildModule();
        var classDecl = new ClassDecl
        {
            Name = "MyClassError",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClassError"),
            MangledName = "",
            IsFinal = true,
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: SwiftTypeName.FromModuleQualifiedName("TestModule.MyClassError"),
                    Protocol: SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                    ProtocolConformanceDescriptor: ""),
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(classDecl);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.MyClassError", out _));
    }

    [Fact]
    public void Precompute_CaselessNamespaceEnumWithLocalizedError_NotRegistered()
    {
        // WeatherKit-shaped pattern: a caseless namespace enum (used as a container for
        // `static let` constants) that conforms to LocalizedError via extension. The C#
        // emission projects a caseless enum as a `static class`, which can't be a generic
        // type argument and can't be cast to. The cascade dispatcher would emit
        // SwiftException<StaticClass> code that fails to compile, so the registry must
        // skip these and let the untyped SwiftException fallback handle them at runtime.
        var moduleDecl = BuildModule();
        var caselessEnum = new EnumDecl
        {
            Name = "WeatherErrorNamespace",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.WeatherErrorNamespace"),
            MangledName = "",
            IsFrozen = false,
            Cases = new(), // <-- Caseless: zero cases.
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: SwiftTypeName.FromModuleQualifiedName("TestModule.WeatherErrorNamespace"),
                    Protocol: SwiftTypeName.FromModuleQualifiedName("Foundation.LocalizedError"),
                    ProtocolConformanceDescriptor: ""),
            },
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(caselessEnum);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.False(ctx.TryGetErrorTypeId("TestModule.WeatherErrorNamespace", out _));
        Assert.Empty(ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_NonErrorEnum_NotRegistered()
    {
        var moduleDecl = BuildModule();
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "Color", "Swift.Equatable"));

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.False(ctx.TryGetErrorTypeId("TestModule.Color", out _));
        Assert.Empty(ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_FoundationLocalizedError_Detected()
    {
        // WeatherKit's WeatherError conforms to Foundation.LocalizedError (which
        // refines Swift.Error). The parser may surface only the LocalizedError side
        // depending on swiftinterface flavor, so the detector must accept it
        // independently of the bare Swift.Error conformance.
        var moduleDecl = BuildModule();
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "WeatherError", "Foundation.LocalizedError"));

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.WeatherError", out _));
    }

    [Fact]
    public void Precompute_NestedErrorEnum_Registered()
    {
        // Swift idiom: extension SomeService { public enum FetchError: Error { ... } }
        // The nested type must be registered with its module-qualified path.
        var moduleDecl = BuildModule();
        var outerStruct = new StructDecl
        {
            Name = "SomeService",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SomeService"),
            MangledName = "",
            IsFrozen = false,
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        var nestedEnum = new EnumDecl
        {
            Name = "FetchError",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SomeService.FetchError"),
            MangledName = "",
            IsFrozen = true,
            // At least one case — caseless enums are filtered out as
            // namespace-only types by the registry.
            Cases = new()
            {
                new EnumCaseDecl
                {
                    Name = "notFound",
                    MangledName = "",
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                },
            },
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: SwiftTypeName.FromModuleQualifiedName("TestModule.SomeService.FetchError"),
                    Protocol: SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                    ProtocolConformanceDescriptor: ""),
            },
            MetadataAccessor = "",
            ParentDecl = outerStruct,
            ModuleDecl = moduleDecl,
        };
        outerStruct.Types.Add(nestedEnum);
        moduleDecl.Types.Add(outerStruct);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.SomeService.FetchError", out var id));
        Assert.Equal(1, id);
        // Outer non-error struct was not registered.
        Assert.False(ctx.TryGetErrorTypeId("TestModule.SomeService", out _));
    }

    [Fact]
    public void Precompute_Idempotent_SecondCallNoOp()
    {
        var moduleDecl = BuildModule();
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "WeatherError", "Swift.Error"));

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);
        // Add another error AFTER the first precompute; second call must not re-walk.
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "LateError", "Swift.Error"));
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.WeatherError", out _));
        Assert.False(ctx.TryGetErrorTypeId("TestModule.LateError", out _));
        Assert.True(ctx.ErrorTypeRegistryComputed);
    }

    [Fact]
    public void RegisterErrorTypeId_DuplicateRegistration_ReturnsExistingId()
    {
        var ctx = new ModuleEmissionContext();
        var firstId = ctx.RegisterErrorTypeId("TestModule.SomeError");
        var secondId = ctx.RegisterErrorTypeId("TestModule.SomeError");

        Assert.Equal(1, firstId);
        Assert.Equal(firstId, secondId);
        Assert.Single(ctx.ErrorTypeIds);
    }

    [Fact]
    public void ConformsToError_ProtocolDecl_ReturnsFalse()
    {
        // Protocols themselves go through moduleDecl.Protocols, not Types.
        // ConformsToError is shape-gated to concrete-type decls only — a
        // ProtocolDecl reaching this helper would return false because it has no
        // Conformances surface in the EnumDecl/StructDecl/ClassDecl sense.
        var protocolDecl = new ProtocolDecl
        {
            Name = "MyErrorProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyErrorProtocol"),
            MangledName = "",
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        Assert.False(ErrorEnumRegistryEmitter.ConformsToError(protocolDecl));
    }

    private static ModuleDecl BuildModule(string name = "TestModule") => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        Properties = new(),
        Methods = new(),
        Types = new(),
        Dependencies = new(),
        Protocols = new(),
    };

    private static EnumDecl BuildErrorEnum(ModuleDecl moduleDecl, string name, string protocolModuleQualifiedName)
    {
        var swiftName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}");
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = swiftName,
            MangledName = "",
            IsFrozen = true,
            // At least one case so the registry's caseless-namespace filter
            // (IsInstantiable) treats this as a real error enum, not a
            // WeatherKit-shaped static namespace.
            Cases = new()
            {
                new EnumCaseDecl
                {
                    Name = "someCase",
                    MangledName = "",
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                },
            },
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: swiftName,
                    Protocol: SwiftTypeName.FromModuleQualifiedName(protocolModuleQualifiedName),
                    ProtocolConformanceDescriptor: ""),
            },
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
    }

}
