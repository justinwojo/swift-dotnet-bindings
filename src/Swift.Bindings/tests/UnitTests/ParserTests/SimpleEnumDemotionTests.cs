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
    public void SimpleEnumUsedAsConstrainedGenericArg_IsDemoted()
    {
        var moduleDecl = CreateModuleDecl("TestModule");

        var enumDecl = CreateEnumDecl("AlertType", "TestModule", isFrozen: true);
        enumDecl.Cases = new List<EnumCaseDecl>
        {
            new EnumCaseDecl { Name = "info", MangledName = "$s_info", ParentDecl = null, ModuleDecl = null },
            new EnumCaseDecl { Name = "warning", MangledName = "$s_warning", ParentDecl = null, ModuleDecl = null },
            new EnumCaseDecl { Name = "error", MangledName = "$s_error", ParentDecl = null, ModuleDecl = null },
        };

        // Generic class with protocol conformance constraint on U (requires ISwiftObject-like behavior)
        var genericClass = CreateClassDecl("ConstrainedBox", "TestModule");
        genericClass.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()),
            new GenericArgumentDecl("τ_0_1", "U", new List<GenericParameterConformance>
            {
                new GenericParameterConformance(new[] { "" }, SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"), ConformanceKind.Protocol)
            }, new List<GenericParameterConformance>()),
        };

        // Property with bound generic type: ConstrainedBox<String, AlertType>
        var boundGenericProp = new PropertyDecl
        {
            Name = "result",
            ParentDecl = genericClass,
            ModuleDecl = moduleDecl,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.ConstrainedBox",
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
            [new NamedTypeSpec("TestModule.ConstrainedBox")] = genericClass,
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
            "Simple enum used as constrained generic type argument should be demoted");
    }

    [Fact]
    public void SimpleEnumAtDroppedMarkerConstrainedPosition_IsDemoted()
    {
        // The regression mechanism end-to-end: a generic parameter constrained only by a
        // module-qualified marker (`where U : Swift.Sendable`) has that marker dropped by the parser,
        // leaving GenericConformances EMPTY but HasDroppedNominalMarkerConstraint set. The demotion
        // gate must still treat the position as constrained, so a simple enum used there is demoted to
        // a class. Without the flag the empty conformance list would read as unconstrained and the enum
        // would regress to a bare C# enum that cannot implement its protocol's interface.
        var moduleDecl = CreateModuleDecl("TestModule");

        var enumDecl = CreateEnumDecl("AlertType", "TestModule", isFrozen: true);
        enumDecl.Cases = new List<EnumCaseDecl>
        {
            new EnumCaseDecl { Name = "info", MangledName = "$s_info", ParentDecl = null, ModuleDecl = null },
        };

        var genericClass = CreateClassDecl("ConstrainedBox", "TestModule");
        genericClass.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()),
            // U carried `: Swift.Sendable`, dropped by the parser → empty conformances, flag set.
            new GenericArgumentDecl("τ_0_1", "U", new List<GenericParameterConformance>(), new List<GenericParameterConformance>(),
                HasDroppedNominalMarkerConstraint: true),
        };

        var boundGenericProp = new PropertyDecl
        {
            Name = "result",
            ParentDecl = genericClass,
            ModuleDecl = moduleDecl,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.ConstrainedBox",
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
            [new NamedTypeSpec("TestModule.ConstrainedBox")] = genericClass,
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
            "Simple enum at a dropped-marker-constrained generic position should be demoted");
    }

    [Fact]
    public void SimpleEnumUsedAsUnconstrainedGenericArg_RetainsSimpleFlag()
    {
        // When generic parameter has no protocol conformances, the enum doesn't need demotion
        var moduleDecl = CreateModuleDecl("TestModule");

        var enumDecl = CreateEnumDecl("AlertType", "TestModule", isFrozen: true);
        enumDecl.Cases = new List<EnumCaseDecl>
        {
            new EnumCaseDecl { Name = "info", MangledName = "$s_info", ParentDecl = null, ModuleDecl = null },
        };

        var genericClass = CreateClassDecl("Container", "TestModule");
        genericClass.GenericParameters = new List<GenericArgumentDecl>
        {
            // No conformances — unconstrained generic parameter
            new GenericArgumentDecl("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()),
        };

        var boundGenericProp = new PropertyDecl
        {
            Name = "value",
            ParentDecl = genericClass,
            ModuleDecl = moduleDecl,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.Container",
                new NamedTypeSpec("TestModule.AlertType")),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
        };
        genericClass.Properties.Add(boundGenericProp);

        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            [new NamedTypeSpec("TestModule.AlertType")] = enumDecl,
            [new NamedTypeSpec("TestModule.Container")] = genericClass,
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
        Assert.True(record!.Flags.HasFlag(TypeRecordFlags.SimpleEnum),
            "Simple enum used as unconstrained generic arg should retain SimpleEnum flag");
    }

    [Fact]
    public void SimpleEnumInStdlibContainer_RetainsSimpleFlag()
    {
        // Enums in Array<T>, Optional<T> etc. should NOT be demoted — these containers
        // are projected to constraint-free C# types (IReadOnlyList<T>, T?)
        var moduleDecl = CreateModuleDecl("TestModule");

        var enumDecl = CreateEnumDecl("Direction", "TestModule", isFrozen: true);
        enumDecl.Cases = new List<EnumCaseDecl>
        {
            new EnumCaseDecl { Name = "north", MangledName = "$s_north", ParentDecl = null, ModuleDecl = null },
        };

        var classDecl = CreateClassDecl("MyType", "TestModule");
        var boundGenericProp = new PropertyDecl
        {
            Name = "directions",
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Array",
                new NamedTypeSpec("TestModule.Direction")),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
        };
        classDecl.Properties.Add(boundGenericProp);

        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            [new NamedTypeSpec("TestModule.Direction")] = enumDecl,
            [new NamedTypeSpec("TestModule.MyType")] = classDecl,
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
            "Simple enum in Swift.Array should retain SimpleEnum flag (projected to IReadOnlyList<T>)");
    }

    [Theory]
    [InlineData(false, "Int", SimpleEnumStorage.CaseTag)]
    [InlineData(true, "Int", SimpleEnumStorage.RawValue)]
    [InlineData(false, null, SimpleEnumStorage.CaseTag)]
    public void SimpleEnum_RecordsHowSwiftStoresIt(bool isObjC, string? rawValueType, SimpleEnumStorage expected)
    {
        // An @objc enum is a C enum, stored as its raw value; any other Swift enum stores its case
        // tag. The record says which for every simple enum, so a module binding against this one reads
        // a payload slot holding it at the right width without a parse of it.
        var enumDecl = CreateEnumDecl("Shade", "TestModule", isFrozen: true);
        enumDecl.RawValueTypeName = rawValueType;
        enumDecl.IsObjC = isObjC;
        enumDecl.Cases = new List<EnumCaseDecl>
        {
            new EnumCaseDecl { Name = "light", RawValue = rawValueType is null ? null : "1", MangledName = "$s_light", ParentDecl = null, ModuleDecl = null },
            new EnumCaseDecl { Name = "dark", RawValue = rawValueType is null ? null : "2", MangledName = "$s_dark", ParentDecl = null, ModuleDecl = null },
        };
        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            [new NamedTypeSpec("TestModule.Shade")] = enumDecl,
        };

        var processor = new ModuleProcessor("TestModule", "/tmp/dummy.dylib", "TestModule", typeDecls, new TypeDatabase(), NullLogger.Instance);
        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();

        Assert.True(result.ModuleDatabase.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName("TestModule.Shade"), out var record));
        Assert.True(record!.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
        Assert.Equal(expected, record.SimpleEnumStorage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntRawValueEnum_CrossesAsCaseTagOnlyWhileASimpleEnum(bool usedAsConstrainedGenericArg)
    {
        // A non-@objc `enum E: Int` crosses as its case tag, range-checked against its case count. Once
        // demoted to a class projection it hands Swift `.RawValue`, so its record must stop saying
        // it crosses as the tag (a SwiftUI bridge would otherwise decode the raw value as a tag).
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateEnumDecl("Shade", "TestModule", isFrozen: true);
        enumDecl.RawValueTypeName = "Int";
        enumDecl.Cases = new List<EnumCaseDecl>
        {
            new EnumCaseDecl { Name = "light", RawValue = "1", MangledName = "$s_light", ParentDecl = null, ModuleDecl = null },
            new EnumCaseDecl { Name = "dark", RawValue = "2", MangledName = "$s_dark", ParentDecl = null, ModuleDecl = null },
        };
        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            [new NamedTypeSpec("TestModule.Shade")] = enumDecl,
        };
        if (usedAsConstrainedGenericArg)
        {
            var genericClass = CreateClassDecl("ConstrainedBox", "TestModule");
            genericClass.GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(new[] { "" }, SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"), ConformanceKind.Protocol)
                }, new List<GenericParameterConformance>()),
            };
            genericClass.Properties.Add(new PropertyDecl
            {
                Name = "result",
                ParentDecl = genericClass,
                ModuleDecl = moduleDecl,
                SwiftTypeSpec = new NamedTypeSpec("TestModule.ConstrainedBox", new NamedTypeSpec("TestModule.Shade")),
                HasStorage = true,
                IsStatic = false,
                Accessors = new List<AccessorDecl>(),
            });
            typeDecls[new NamedTypeSpec("TestModule.ConstrainedBox")] = genericClass;
        }

        var processor = new ModuleProcessor("TestModule", "/tmp/dummy.dylib", "TestModule", typeDecls, new TypeDatabase(), NullLogger.Instance);
        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();

        Assert.True(result.ModuleDatabase.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName("TestModule.Shade"), out var record));
        Assert.Equal(!usedAsConstrainedGenericArg, record!.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
        Assert.Equal(!usedAsConstrainedGenericArg, record.RawValueCrossesAsCaseTag);
        Assert.Equal(usedAsConstrainedGenericArg ? 0 : 2, record.CaseTagCount);
        Assert.Equal(usedAsConstrainedGenericArg, SimpleEnumCrossing.CrossesAsRawValue(record));
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
