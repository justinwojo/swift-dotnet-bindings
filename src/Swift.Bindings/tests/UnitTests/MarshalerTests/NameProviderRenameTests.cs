// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regression tests for NameProvider property rename logic.
/// Validates that ComputePropertyRenames correctly computes member renames
/// without modifying TypeDatabase records.
/// </summary>
public class NameProviderRenameTests
{
    #region ComputePropertyRenamesForNestedTypeCollisions Tests

    [Fact]
    public void ComputePropertyRenamesForNestedTypeCollisions_CollidingName_ReturnsValueSuffix()
    {
        var memberNames = new[] { "Cache", "Name" };
        var nestedTypeNames = new[] { "Cache", "Settings" };

        var renames = NameProvider.ComputePropertyRenamesForNestedTypeCollisions(memberNames, nestedTypeNames);

        Assert.Single(renames);
        Assert.Equal("CacheValue", renames["Cache"]);
    }

    [Fact]
    public void ComputePropertyRenamesForNestedTypeCollisions_NoCollision_ReturnsEmpty()
    {
        var memberNames = new[] { "Name", "Value" };
        var nestedTypeNames = new[] { "Settings", "Options" };

        var renames = NameProvider.ComputePropertyRenamesForNestedTypeCollisions(memberNames, nestedTypeNames);

        Assert.Empty(renames);
    }

    [Fact]
    public void ComputePropertyRenamesForNestedTypeCollisions_ValueSuffixAlreadyExists_UsesIncrementingSuffix()
    {
        // Member "Cache" collides with nested type "Cache", but "CacheValue" already exists as a member.
        // The rename should fall back to "CacheValue2" to avoid a duplicate.
        var memberNames = new[] { "Cache", "CacheValue" };
        var nestedTypeNames = new[] { "Cache" };

        var renames = NameProvider.ComputePropertyRenamesForNestedTypeCollisions(memberNames, nestedTypeNames);

        Assert.Single(renames);
        Assert.Equal("CacheValue2", renames["Cache"]);
    }

    [Fact]
    public void ComputePropertyRenamesForNestedTypeCollisions_ValueSuffixIsNestedType_UsesIncrementingSuffix()
    {
        // Member "Foo" collides with nested type "Foo", and "FooValue" also exists as a nested type.
        var memberNames = new[] { "Foo" };
        var nestedTypeNames = new[] { "Foo", "FooValue" };

        var renames = NameProvider.ComputePropertyRenamesForNestedTypeCollisions(memberNames, nestedTypeNames);

        Assert.Single(renames);
        Assert.Equal("FooValue2", renames["Foo"]);
    }

    [Fact]
    public void ComputePropertyRenamesForNestedTypeCollisions_TwoCollisions_RenamesBothIndependently()
    {
        var memberNames = new[] { "Cache", "Name" };
        var nestedTypeNames = new[] { "Cache", "Name" };

        var renames = NameProvider.ComputePropertyRenamesForNestedTypeCollisions(memberNames, nestedTypeNames);

        Assert.Equal(2, renames.Count);
        Assert.Equal("CacheValue", renames["Cache"]);
        Assert.Equal("NameValue", renames["Name"]);
    }

    #endregion

    #region ComputePropertyRenames Tests

    [Fact]
    public void ComputePropertyRenames_CollidingProperty_ReturnsRename_DoesNotModifyTypeDatabase()
    {
        // Scenario: ImagePipeline has property "cache" (PascalCase: "Cache")
        // and nested type "Cache" which itself has nested type "Entry".
        // Property "Cache" should be renamed to "CacheValue".
        // TypeDatabase should NOT be modified.
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
        var entryDecl = CreateStructDecl("Entry", entrySwiftName, moduleDecl);
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
        var renames = NameProvider.ComputePropertyRenames(parentDecl, typeDatabase);

        // Assert — property "Cache" should be renamed to "CacheValue"
        Assert.Single(renames);
        Assert.Equal("CacheValue", renames["Cache"]);

        // Assert — TypeDatabase should NOT be modified
        Assert.True(typeDatabase.TryGetTypeRecord(cacheSwiftName, out var cacheRecord));
        Assert.Equal("ImagePipeline.Cache", cacheRecord!.CSharpTypeName.Name);

        Assert.True(typeDatabase.TryGetTypeRecord(entrySwiftName, out var entryRecord));
        Assert.Equal("ImagePipeline.Cache.Entry", entryRecord!.CSharpTypeName.Name);
    }

    [Fact]
    public void ComputePropertyRenames_NoCollision_ReturnsEmpty_LeavesTypesDatabaseUnchanged()
    {
        // When there's no property/type name collision, no renames should be generated.
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
        var renames = NameProvider.ComputePropertyRenames(parentDecl, typeDatabase);

        // Assert — no renames, child name unchanged
        Assert.Empty(renames);
        Assert.True(typeDatabase.TryGetTypeRecord(childSwiftName, out var childRecord));
        Assert.Equal("Parent.Settings", childRecord!.CSharpTypeName.Name);
    }

    #endregion

    #region GetFinalMemberName Tests

    [Fact]
    public void GetFinalMemberName_WithRename_ReturnsRenamed()
    {
        var renames = new Dictionary<string, string> { { "Cache", "CacheValue" } };
        Assert.Equal("CacheValue", NameProvider.GetFinalMemberName("Cache", renames));
    }

    [Fact]
    public void GetFinalMemberName_WithoutRename_ReturnsOriginal()
    {
        var renames = new Dictionary<string, string> { { "Cache", "CacheValue" } };
        Assert.Equal("Name", NameProvider.GetFinalMemberName("Name", renames));
    }

    [Fact]
    public void GetFinalMemberName_NullRenames_ReturnsOriginal()
    {
        Assert.Equal("Cache", NameProvider.GetFinalMemberName("Cache", null));
    }

    #endregion

    #region GetPropertyName CS0542 Tests

    [Fact]
    public void GetPropertyName_CS0542_PascalCaseMatch_AppendsSuffix()
    {
        // Swift: class Config { var config: String }
        // PascalCase "config" → "Config" collides with containing type "Config" → CS0542
        var result = NameProvider.GetPropertyName("config", containingTypeName: "Config");
        Assert.Equal("ConfigValue", result);
    }

    [Fact]
    public void GetPropertyName_CS0542_ExactMatch_AppendsSuffix()
    {
        // Swift: class Animation { var Animation: Animation? }
        // Already PascalCase, exact match with containing type
        var result = NameProvider.GetPropertyName("Animation", containingTypeName: "Animation");
        Assert.Equal("AnimationValue", result);
    }

    [Fact]
    public void GetPropertyName_NoCS0542_DifferentName_NoSuffix()
    {
        // Swift: class MyType { var data: Data }
        // PascalCase "data" → "Data" does NOT match "MyType" → no suffix
        var result = NameProvider.GetPropertyName("data", containingTypeName: "MyType");
        Assert.Equal("Data", result);
    }

    [Fact]
    public void GetPropertyName_NullContainingType_NoSuffix()
    {
        // Module-level property — no containing type, no CS0542 possible
        var result = NameProvider.GetPropertyName("config", containingTypeName: null);
        Assert.Equal("Config", result);
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
