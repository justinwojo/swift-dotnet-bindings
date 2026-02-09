// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regression tests for NameProvider nested type rename propagation.
/// Validates that ComputeAndApplyNestedTypeRenames correctly updates
/// both renamed types and their descendant types in the TypeDatabase.
/// </summary>
public class NameProviderRenameTests
{
    #region ComputeNestedTypeRenames Tests

    [Fact]
    public void ComputeNestedTypeRenames_CollidingName_ReturnsInfoSuffix()
    {
        var propertyNames = new[] { "Cache", "Name" };
        var nestedTypeNames = new[] { "Cache", "Settings" };

        var renames = NameProvider.ComputeNestedTypeRenames(propertyNames, nestedTypeNames);

        Assert.Single(renames);
        Assert.Equal("CacheInfo", renames["Cache"]);
    }

    [Fact]
    public void ComputeNestedTypeRenames_NoCollision_ReturnsEmpty()
    {
        var propertyNames = new[] { "Name", "Value" };
        var nestedTypeNames = new[] { "Settings", "Options" };

        var renames = NameProvider.ComputeNestedTypeRenames(propertyNames, nestedTypeNames);

        Assert.Empty(renames);
    }

    #endregion

    #region Descendant Rename Propagation Tests

    [Fact]
    public void ComputeAndApplyNestedTypeRenames_UpdatesDescendantTypes()
    {
        // Scenario: ImagePipeline has property "cache" (PascalCase: "Cache")
        // and nested type "Cache" which itself has nested type "Entry".
        // After rename: Cache → CacheInfo, Cache.Entry → CacheInfo.Entry
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImagePipeline");
        module.RegisterType(parentSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "ImagePipeline"),
            SwiftTypeName = parentSwiftName,
            MetadataAccessor = "$s10TestModule13ImagePipelineVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var cacheSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImagePipeline.Cache");
        module.RegisterType(cacheSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "ImagePipeline.Cache"),
            SwiftTypeName = cacheSwiftName,
            MetadataAccessor = "$s10TestModule13ImagePipelineV5CacheVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var entrySwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImagePipeline.Cache.Entry");
        module.RegisterType(entrySwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "ImagePipeline.Cache.Entry"),
            SwiftTypeName = entrySwiftName,
            MetadataAccessor = "$s10TestModule13ImagePipelineV5CacheV5EntryVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        var moduleDecl = new ModuleDecl
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

        // Build type hierarchy: ImagePipeline → Cache → Entry
        var entryDecl = new StructDecl
        {
            Name = "Entry",
            SwiftTypeName = entrySwiftName,
            MangledName = "$s10TestModule13ImagePipelineV5CacheV5EntryVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule13ImagePipelineV5CacheV5EntryVMa"
        };

        var cacheDecl = new StructDecl
        {
            Name = "Cache",
            SwiftTypeName = cacheSwiftName,
            MangledName = "$s10TestModule13ImagePipelineV5CacheVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { entryDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule13ImagePipelineV5CacheVMa"
        };
        entryDecl.ParentDecl = cacheDecl;

        var parentDecl = new StructDecl
        {
            Name = "ImagePipeline",
            SwiftTypeName = parentSwiftName,
            MangledName = "$s10TestModule13ImagePipelineVN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "cache",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { cacheDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule13ImagePipelineVMa"
        };
        cacheDecl.ParentDecl = parentDecl;

        // Act
        var renames = NameProvider.ComputeAndApplyNestedTypeRenames(parentDecl, typeDatabase);

        // Assert — Cache should be renamed to CacheInfo
        Assert.Single(renames);
        Assert.Equal("CacheInfo", renames["Cache"]);

        Assert.True(typeDatabase.TryGetTypeRecord(cacheSwiftName, out var cacheRecord));
        Assert.Equal("ImagePipeline.CacheInfo", cacheRecord!.CSharpTypeName.Name);

        // Assert — descendant Entry should be updated from Cache.Entry to CacheInfo.Entry
        Assert.True(typeDatabase.TryGetTypeRecord(entrySwiftName, out var entryRecord));
        Assert.Equal("ImagePipeline.CacheInfo.Entry", entryRecord!.CSharpTypeName.Name);
    }

    [Fact]
    public void ComputeAndApplyNestedTypeRenames_DeeplyNested_UpdatesAllDescendants()
    {
        // Scenario: 3-level nesting. Parent has property colliding with nested type,
        // and the nested type has a child which has a grandchild.
        // All descendants must update their C# name prefix.
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer");
        module.RegisterType(parentSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Outer"),
            SwiftTypeName = parentSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var configSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Config");
        module.RegisterType(configSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Outer.Config"),
            SwiftTypeName = configSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var detailSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Config.Detail");
        module.RegisterType(detailSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Outer.Config.Detail"),
            SwiftTypeName = detailSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var flagSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Config.Detail.Flag");
        module.RegisterType(flagSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Outer.Config.Detail.Flag"),
            SwiftTypeName = flagSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        var moduleDecl = new ModuleDecl
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

        // Build hierarchy: Outer → Config → Detail → Flag
        var flagDecl = CreateStructDecl("Flag", flagSwiftName, moduleDecl);
        var detailDecl = CreateStructDecl("Detail", detailSwiftName, moduleDecl);
        detailDecl.Types = new List<TypeDecl> { flagDecl };
        flagDecl.ParentDecl = detailDecl;

        var configDecl = CreateStructDecl("Config", configSwiftName, moduleDecl);
        configDecl.Types = new List<TypeDecl> { detailDecl };
        detailDecl.ParentDecl = configDecl;

        var outerDecl = new StructDecl
        {
            Name = "Outer",
            SwiftTypeName = parentSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "config",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { configDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        configDecl.ParentDecl = outerDecl;

        // Act
        NameProvider.ComputeAndApplyNestedTypeRenames(outerDecl, typeDatabase);

        // Assert — Config renamed, all descendants updated
        Assert.True(typeDatabase.TryGetTypeRecord(configSwiftName, out var configRecord));
        Assert.Equal("Outer.ConfigInfo", configRecord!.CSharpTypeName.Name);

        Assert.True(typeDatabase.TryGetTypeRecord(detailSwiftName, out var detailRecord));
        Assert.Equal("Outer.ConfigInfo.Detail", detailRecord!.CSharpTypeName.Name);

        Assert.True(typeDatabase.TryGetTypeRecord(flagSwiftName, out var flagRecord));
        Assert.Equal("Outer.ConfigInfo.Detail.Flag", flagRecord!.CSharpTypeName.Name);
    }

    [Fact]
    public void ComputeAndApplyNestedTypeRenames_NoCollision_LeavesDescendantsUnchanged()
    {
        // When there's no property/type name collision, descendant types should not be modified.
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Parent");
        module.RegisterType(parentSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Parent"),
            SwiftTypeName = parentSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var childSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Parent.Settings");
        module.RegisterType(childSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Parent.Settings"),
            SwiftTypeName = childSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        var moduleDecl = new ModuleDecl
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

        var childDecl = CreateStructDecl("Settings", childSwiftName, moduleDecl);
        var parentDecl = new StructDecl
        {
            Name = "Parent",
            SwiftTypeName = parentSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "name",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { childDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        childDecl.ParentDecl = parentDecl;

        // Act
        var renames = NameProvider.ComputeAndApplyNestedTypeRenames(parentDecl, typeDatabase);

        // Assert — no renames, child name unchanged
        Assert.Empty(renames);
        Assert.True(typeDatabase.TryGetTypeRecord(childSwiftName, out var childRecord));
        Assert.Equal("Parent.Settings", childRecord!.CSharpTypeName.Name);
    }

    #endregion

    #region PrecomputeAllNestedTypeRenames Tests

    [Fact]
    public void PrecomputeAllNestedTypeRenames_ProcessesNestedTypesRecursively()
    {
        // Scenario: Module has type A with nested type B that collides with A's property.
        // B itself has nested type C that collides with B's property.
        // PrecomputeAllNestedTypeRenames should handle both levels.
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        var aSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.A");
        module.RegisterType(aSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "A"),
            SwiftTypeName = aSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var bSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.A.B");
        module.RegisterType(bSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "A.B"),
            SwiftTypeName = bSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var cSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.A.B.C");
        module.RegisterType(cSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "A.B.C"),
            SwiftTypeName = cSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        var moduleDecl = new ModuleDecl
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

        // B has property "c" colliding with nested type C
        var cDecl = CreateStructDecl("C", cSwiftName, moduleDecl);
        var bDecl = new StructDecl
        {
            Name = "B",
            SwiftTypeName = bSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "c",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { cDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        cDecl.ParentDecl = bDecl;

        // A has property "b" colliding with nested type B
        var aDecl = new StructDecl
        {
            Name = "A",
            SwiftTypeName = aSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "b",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { bDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        bDecl.ParentDecl = aDecl;

        // Act — process entire module
        NameProvider.PrecomputeAllNestedTypeRenames(new[] { aDecl }, typeDatabase);

        // Assert — B renamed to BInfo at level 1
        Assert.True(typeDatabase.TryGetTypeRecord(bSwiftName, out var bRecord));
        Assert.Equal("A.BInfo", bRecord!.CSharpTypeName.Name);

        // Assert — C renamed to CInfo at level 2, and its parent prefix updated from B → BInfo
        Assert.True(typeDatabase.TryGetTypeRecord(cSwiftName, out var cRecord));
        Assert.Equal("A.BInfo.CInfo", cRecord!.CSharpTypeName.Name);
    }

    #endregion

    #region Helpers

    private static StructDecl CreateStructDecl(string name, SwiftTypeName swiftName, ModuleDecl moduleDecl)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = swiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
    }

    #endregion
}
