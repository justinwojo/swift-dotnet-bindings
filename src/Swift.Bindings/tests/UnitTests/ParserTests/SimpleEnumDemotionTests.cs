// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the ModuleProcessor post-scan pass that demotes simple enums
/// when they are used as generic type arguments (C# enums can't implement ISwiftObject).
/// </summary>
public class SimpleEnumDemotionTests
{
    [Fact]
    public void SimpleEnumUsedAsGenericArg_IsDemoted()
    {
        var moduleDecl = CreateModuleDecl("TestModule");

        var enumDecl = CreateEnumDecl("AlertType", "TestModule", isFrozen: true);
        enumDecl.Cases = new List<EnumCaseDecl>
        {
            new EnumCaseDecl { Name = "info", MangledName = "$s_info", ParentDecl = null, ModuleDecl = null },
            new EnumCaseDecl { Name = "warning", MangledName = "$s_warning", ParentDecl = null, ModuleDecl = null },
            new EnumCaseDecl { Name = "error", MangledName = "$s_error", ParentDecl = null, ModuleDecl = null },
        };

        var genericClass = CreateClassDecl("ScanningResult", "TestModule");
        genericClass.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()),
            new GenericArgumentDecl("τ_0_1", "U", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()),
        };

        // Property with bound generic type: ScanningResult<String, AlertType>
        var boundGenericProp = new PropertyDecl
        {
            Name = "result",
            ParentDecl = genericClass,
            ModuleDecl = moduleDecl,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.ScanningResult",
                new NamedTypeSpec("Swift.String"),
                new NamedTypeSpec("TestModule.AlertType")),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
        };
        genericClass.Properties.Add(boundGenericProp);

        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            [new NamedTypeSpec("TestModule.AlertType")] = enumDecl,
            [new NamedTypeSpec("TestModule.ScanningResult")] = genericClass,
        };

        var typeDatabase = new TypeDatabase();
        var processor = new ModuleProcessor(
            "TestModule",
            "/tmp/dummy.dylib",
            "TestModule",
            typeDecls,
            typeDatabase,
            NullLogger.Instance);

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AlertType");
        Assert.True(result.ModuleDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.False(record!.Flags.HasFlag(TypeRecordFlags.SimpleEnum),
            "Simple enum used as generic type argument should be demoted (C# enums can't implement ISwiftObject)");
    }

    [Fact]
    public void SimpleEnumNotUsedAsGenericArg_RetainsSimpleFlag()
    {
        var enumDecl = CreateEnumDecl("Direction", "TestModule", isFrozen: true);
        enumDecl.Cases = new List<EnumCaseDecl>
        {
            new EnumCaseDecl { Name = "north", MangledName = "$s_north", ParentDecl = null, ModuleDecl = null },
            new EnumCaseDecl { Name = "south", MangledName = "$s_south", ParentDecl = null, ModuleDecl = null },
        };

        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            [new NamedTypeSpec("TestModule.Direction")] = enumDecl,
        };

        var typeDatabase = new TypeDatabase();
        var processor = new ModuleProcessor(
            "TestModule",
            "/tmp/dummy.dylib",
            "TestModule",
            typeDecls,
            typeDatabase,
            NullLogger.Instance);

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Direction");
        Assert.True(result.ModuleDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.True(record!.Flags.HasFlag(TypeRecordFlags.SimpleEnum),
            "Simple enum not used as generic type argument should retain SimpleEnum flag");
    }

    #region Helpers

    private static ClassDecl CreateClassDecl(string name, string moduleName)
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
            SuperclassNames = new List<string>(),
        };
    }

    private static EnumDecl CreateEnumDecl(string name, string moduleName, bool isFrozen = false)
    {
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}ON",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Cases = new List<EnumCaseDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = isFrozen,
            MetadataAccessor = "",
        };
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
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
    }

    #endregion
}
