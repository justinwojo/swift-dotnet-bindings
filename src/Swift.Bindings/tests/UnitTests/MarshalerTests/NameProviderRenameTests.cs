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

    [Fact]
    public void ComputePropertyRenamesForNestedTypeCollisions_TypeRenamed_SkipsPropertyRename()
    {
        // When a nested type was renamed (e.g., Configuration → ConfigurationInfo), the
        // property should NOT be renamed — the collision is resolved by the type rename.
        var memberNames = new[] { "Configuration", "Name" };
        var nestedTypeNames = new[] { "Configuration", "Settings" };
        var typeRenameNames = new HashSet<string> { "Configuration" };

        var renames = NameProvider.ComputePropertyRenamesForNestedTypeCollisions(
            memberNames, nestedTypeNames, typeRenameNames);

        // "Configuration" should NOT be renamed (nested type was renamed instead)
        Assert.Empty(renames);
    }

    [Fact]
    public void ComputePropertyRenamesForNestedTypeCollisions_MixedTypeRenameAndNon_RenamesOnlyUnresolved()
    {
        // "Configuration" was type-renamed, but "Cache" was not → "Cache" still needs property rename.
        var memberNames = new[] { "Configuration", "Cache" };
        var nestedTypeNames = new[] { "Configuration", "Cache" };
        var typeRenameNames = new HashSet<string> { "Configuration" };

        var renames = NameProvider.ComputePropertyRenamesForNestedTypeCollisions(
            memberNames, nestedTypeNames, typeRenameNames);

        Assert.Single(renames);
        Assert.Equal("CacheValue", renames["Cache"]);
        Assert.False(renames.ContainsKey("Configuration"));
    }

    #endregion

    #region GetTypeSpecLeafName Tests

    [Fact]
    public void GetTypeSpecLeafName_SimpleNamedType_ReturnsName()
    {
        var typeSpec = new NamedTypeSpec("Configuration");
        Assert.Equal("Configuration", NameProvider.GetTypeSpecLeafName(typeSpec));
    }

    [Fact]
    public void GetTypeSpecLeafName_ModuleQualifiedType_ReturnsLeafName()
    {
        var typeSpec = new NamedTypeSpec("ImagePipeline.ImageService");
        Assert.Equal("ImageService", NameProvider.GetTypeSpecLeafName(typeSpec));
    }

    [Fact]
    public void GetTypeSpecLeafName_InnerTypeChain_ReturnsInnerLeaf()
    {
        var typeSpec = new NamedTypeSpec("ImagePipeline.ImageService")
        {
            InnerType = new NamedTypeSpec("Configuration")
        };
        Assert.Equal("Configuration", NameProvider.GetTypeSpecLeafName(typeSpec));
    }

    [Fact]
    public void GetTypeSpecLeafName_OptionalWrapped_UnwrapsAndReturnsLeaf()
    {
        var innerTypeSpec = new NamedTypeSpec("ImagePipeline.ImageService")
        {
            InnerType = new NamedTypeSpec("Configuration")
        };
        var optionalSpec = new NamedTypeSpec("Swift.Optional", innerTypeSpec);
        Assert.Equal("Configuration", NameProvider.GetTypeSpecLeafName(optionalSpec));
    }

    [Fact]
    public void GetTypeSpecLeafName_NonNamedType_ReturnsNull()
    {
        var typeSpec = new TupleTypeSpec();
        Assert.Null(NameProvider.GetTypeSpecLeafName(typeSpec));
    }

    #endregion

    #region ComputePropertyRenames Tests

    [Fact]
    public void ComputePropertyRenames_PropertyTypeIsNestedType_RenamesTypeNotProperty()
    {
        // Property "configuration" (PascalCase: "Configuration") collides with nested type "Configuration".
        // Since the property type IS the nested type, rename the nested TYPE — a struct, so the
        // kind-aware suffix is "Info" → "ConfigurationInfo" — instead of renaming the PROPERTY
        // (better consumer ergonomics).
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImagePipeline");
        module.RegisterType(parentSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ImagePipeline"),
            SwiftTypeName = parentSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var configSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImagePipeline.Configuration");
        module.RegisterType(configSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ImagePipeline.Configuration"),
            SwiftTypeName = configSwiftName,
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

        var configDecl = CreateStructDecl("Configuration", configSwiftName, moduleDecl);
        var parentDecl = new StructDecl
        {
            Name = "ImagePipeline",
            SwiftTypeName = parentSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "configuration",
                    // Type IS the nested type → rename type, not property
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.ImagePipeline")
                    {
                        InnerType = new NamedTypeSpec("Configuration")
                    },
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
        configDecl.ParentDecl = parentDecl;
        moduleDecl.Types.Add(parentDecl);

        // Pre-pass applies the CSharpTypeName rename
        NameProvider.PrecomputeNestedTypeRenames(moduleDecl, typeDatabase);
        var renames = NameProvider.ComputePropertyRenames(parentDecl, typeDatabase);

        // Property should NOT be renamed — nested type was renamed instead
        Assert.Empty(renames);

        // Nested type's CSharpTypeName should be updated to "ConfigurationInfo" (struct → "Info")
        Assert.True(typeDatabase.TryGetTypeRecord(configSwiftName, out var configRecord));
        Assert.Equal("ImagePipeline.ConfigurationInfo", configRecord!.CSharpTypeName.Name);
    }

    [Fact]
    public void ComputePropertyRenames_PropertyTypeIsNestedType_CascadesRenameToChildren()
    {
        // When nested type "Cache" (a struct → "Info" suffix) is renamed to "CacheInfo", its child
        // types must also be updated. E.g., "ImagePipeline.Cache.Caches" → "ImagePipeline.CacheInfo.Caches"
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImagePipeline");
        module.RegisterType(parentSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ImagePipeline"),
            SwiftTypeName = parentSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var cacheSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImagePipeline.Cache");
        module.RegisterType(cacheSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ImagePipeline.Cache"),
            SwiftTypeName = cacheSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var cachesSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImagePipeline.Cache.Caches");
        module.RegisterType(cachesSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ImagePipeline.Cache.Caches"),
            SwiftTypeName = cachesSwiftName,
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

        var cachesDecl = CreateStructDecl("Caches", cachesSwiftName, moduleDecl);
        var cacheDecl = new StructDecl
        {
            Name = "Cache",
            SwiftTypeName = cacheSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { cachesDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        cachesDecl.ParentDecl = cacheDecl;

        var parentDecl = new StructDecl
        {
            Name = "ImagePipeline",
            SwiftTypeName = parentSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "cache",
                    // Type IS the nested type → rename type, not property
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.ImagePipeline")
                    {
                        InnerType = new NamedTypeSpec("Cache")
                    },
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
            MetadataAccessor = "$sMa"
        };
        cacheDecl.ParentDecl = parentDecl;
        moduleDecl.Types.Add(parentDecl);

        // Pre-pass applies the CSharpTypeName rename and cascades to children
        NameProvider.PrecomputeNestedTypeRenames(moduleDecl, typeDatabase);
        var renames = NameProvider.ComputePropertyRenames(parentDecl, typeDatabase);

        // Property not renamed
        Assert.Empty(renames);

        // Nested type renamed (struct → "Info")
        Assert.True(typeDatabase.TryGetTypeRecord(cacheSwiftName, out var cacheRecord));
        Assert.Equal("ImagePipeline.CacheInfo", cacheRecord!.CSharpTypeName.Name);

        // Child type also renamed (cascaded)
        Assert.True(typeDatabase.TryGetTypeRecord(cachesSwiftName, out var cachesRecord));
        Assert.Equal("ImagePipeline.CacheInfo.Caches", cachesRecord!.CSharpTypeName.Name);
    }

    [Fact]
    public void ComputePropertyRenames_TwoCollisions_AvoidsSecondaryRenameClash()
    {
        // Regression for the StoreKit Transaction case (CS0102):
        //   struct Transaction {
        //     var offer: Offer
        //     var offerType: OfferType
        //     struct Offer { ... }   // data aggregate
        //     enum OfferType { ... } // closed case-set
        //   }
        // Both nested types collide with a same-named property, so both are renamed. The
        // kind-aware scheme picks the suffix from each type's kind: the `Offer` STRUCT gets
        // "Info" → OfferInfo, the `OfferType` ENUM gets "Kind" → OfferTypeKind. The two are
        // obviously distinct and neither reads as a numbered variant of the other — the cascade
        // resolves with no numeric suffix at all, where the old scheme produced the misleading
        // OfferType2/OfferType3 family (and the pre-b6d1ba50 scheme the OfferTypeType stutter).
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Transaction");
        module.RegisterType(parentSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Transaction"),
            SwiftTypeName = parentSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var offerSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Transaction.Offer");
        module.RegisterType(offerSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Transaction.Offer"),
            SwiftTypeName = offerSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var offerTypeSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Transaction.OfferType");
        module.RegisterType(offerTypeSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Transaction.OfferType"),
            SwiftTypeName = offerTypeSwiftName,
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

        var offerDecl = CreateStructDecl("Offer", offerSwiftName, moduleDecl);
        // OfferType is a Swift enum in the real StoreKit API — build it as an EnumDecl so the
        // kind-aware suffix picks "Kind", not the struct "Info".
        var offerTypeDecl = CreateEnumDecl("OfferType", offerTypeSwiftName, moduleDecl);

        var parentDecl = new StructDecl
        {
            Name = "Transaction",
            SwiftTypeName = parentSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "offer",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Transaction")
                    {
                        InnerType = new NamedTypeSpec("Offer")
                    },
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new()
                {
                    Name = "offerType",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Transaction")
                    {
                        InnerType = new NamedTypeSpec("OfferType")
                    },
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { offerDecl, offerTypeDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        offerDecl.ParentDecl = parentDecl;
        offerTypeDecl.ParentDecl = parentDecl;
        moduleDecl.Types.Add(parentDecl);

        // Pre-pass applies the CSharpTypeName renames for both nested types.
        NameProvider.PrecomputeNestedTypeRenames(moduleDecl, typeDatabase);

        Assert.True(typeDatabase.TryGetTypeRecord(offerSwiftName, out var offerRecord));
        Assert.True(typeDatabase.TryGetTypeRecord(offerTypeSwiftName, out var offerTypeRecord));

        var offerLeaf = LeafName(offerRecord!.CSharpTypeName.Name);
        var offerTypeLeaf = LeafName(offerTypeRecord!.CSharpTypeName.Name);

        // Kind-aware suffixes resolve the cascade cleanly with no numeric tail: the `Offer` struct
        // gets "Info", the `OfferType` enum gets "Kind".
        Assert.Equal("OfferInfo", offerLeaf);
        Assert.Equal("OfferTypeKind", offerTypeLeaf);

        // Both must be distinct (otherwise CS0102) and neither may reuse an original sibling leaf.
        Assert.NotEqual(offerLeaf, offerTypeLeaf);
        Assert.NotEqual("Offer", offerLeaf);
        Assert.NotEqual("OfferType", offerLeaf);
        Assert.NotEqual("OfferType", offerTypeLeaf);
        Assert.NotEqual("Offer", offerTypeLeaf);

        // Each rename still contains the matching property's PascalCase name so a consumer grepping
        // the Swift name finds it, and neither carries the "TypeType" stutter the scheme removes.
        Assert.StartsWith("Offer", offerLeaf);
        Assert.StartsWith("OfferType", offerTypeLeaf);
        Assert.DoesNotContain("TypeType", offerLeaf);
        Assert.DoesNotContain("TypeType", offerTypeLeaf);
    }

    [Fact]
    public void ComputePropertyRenames_NestedEnumLeafAlreadyEndsInSuffix_DoesNotStutter()
    {
        // Anti-stutter guard: a nested enum whose Swift leaf already ends in the kind-aware
        // suffix ("Kind") must not double into "KindKind". Here `Container.kind` (property) and
        // `Container.Kind` (enum) collide; the enum's kind-aware suffix is itself "Kind", so the
        // naive `leaf + suffix` would emit `KindKind`. The guard keeps the leaf and lets the
        // numeric fallback disambiguate against the colliding property → `Kind2`.
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container");
        module.RegisterType(parentSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Container"),
            SwiftTypeName = parentSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var kindSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container.Kind");
        module.RegisterType(kindSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Container.Kind"),
            SwiftTypeName = kindSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Enum
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

        var kindDecl = CreateEnumDecl("Kind", kindSwiftName, moduleDecl);
        var parentDecl = new StructDecl
        {
            Name = "Container",
            SwiftTypeName = parentSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "kind",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Container")
                    {
                        InnerType = new NamedTypeSpec("Kind")
                    },
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { kindDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        kindDecl.ParentDecl = parentDecl;
        moduleDecl.Types.Add(parentDecl);

        NameProvider.PrecomputeNestedTypeRenames(moduleDecl, typeDatabase);

        Assert.True(typeDatabase.TryGetTypeRecord(kindSwiftName, out var kindRecord));
        var kindLeaf = LeafName(kindRecord!.CSharpTypeName.Name);

        Assert.DoesNotContain("KindKind", kindLeaf);
        Assert.Equal("Kind2", kindLeaf);
    }

    private static string LeafName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 ? fullName.Substring(lastDot + 1) : fullName;
    }

    [Fact]
    public void ComputePropertyRenames_CollidingProperty_ReturnsRename_DoesNotModifyTypeDatabase()
    {
        // Scenario: ImagePipeline has property "cache" (PascalCase: "Cache")
        // and nested type "Cache" which itself has nested type "Entry".
        // Property "Cache" should be renamed to "CacheValue" (CS0102 avoidance).
        // TypeDatabase should NOT be modified.
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImagePipeline");
        module.RegisterType(parentSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ImagePipeline"),
            SwiftTypeName = parentSwiftName,
            MetadataAccessor = "$s10TestModule13ImagePipelineVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var cacheSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImagePipeline.Cache");
        module.RegisterType(cacheSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ImagePipeline.Cache"),
            SwiftTypeName = cacheSwiftName,
            MetadataAccessor = "$s10TestModule13ImagePipelineV5CacheVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var entrySwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImagePipeline.Cache.Entry");
        module.RegisterType(entrySwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ImagePipeline.Cache.Entry"),
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
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Parent"),
            SwiftTypeName = parentSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var childSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Parent.Settings");
        module.RegisterType(childSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Parent.Settings"),
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

    #region GetPropertyName PropertyNameMappings deletion regression Tests

    [Fact]
    public void GetPropertyName_DeletedPropertyNameMappings_StatusNoLongerOverridden()
    {
        // Regression check for the deleted PropertyNameMappings override ("status" → "StatusProperty").
        // With no nested-type context, "status" should now pascal-case to plain "Status",
        // not the historical "StatusProperty".
        var result = NameProvider.GetPropertyName("status");
        Assert.Equal("Status", result);
    }

    [Fact]
    public void GetPropertyName_DeletedPropertyNameMappings_IsEligibleForIntroOfferNoLongerOverridden()
    {
        var result = NameProvider.GetPropertyName("isEligibleForIntroOffer");
        Assert.Equal("IsEligibleForIntroOffer", result);
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

    private static EnumDecl CreateEnumDecl(string name, SwiftTypeName swiftName, ModuleDecl moduleDecl)
    {
        return new EnumDecl
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
            Cases = new List<EnumCaseDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
    }

    #endregion
}
