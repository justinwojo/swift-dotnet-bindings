// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Cross-module propagation of the property/nested-type rename.
///
/// The producer's <see cref="NameProvider.PrecomputeNestedTypeRenames"/> pass
/// detects a parent type that has both a nested type and a property whose
/// PascalCased names collide and renames the nested type with a kind-aware
/// semantic suffix — enum→"Kind", struct/class→"Info". The rename mutates the
/// nested type's <c>TypeRecord.CSharpTypeName</c> in the producer's
/// <c>ModuleTypeDatabase</c>.
///
/// When a consumer module references that same nested type cross-module, the
/// generator must see the renamed C# name. There are two paths into the
/// consumer's <c>TypeDatabase</c>:
///
///   1. <c>--module-database</c> pre-supplies the producer's emitted XML,
///      which already carries the renamed <c>managedTypeName</c> attribute
///      (no extra work needed).
///   2. <c>--framework-dependency</c> triggers an ABI re-parse of the dep
///      inside the consumer's run. The rename pass must run on the
///      re-parsed dep's <c>ModuleDecl</c> too, otherwise the dep
///      <c>TypeRecord</c> keeps the raw Swift leaf name and consumer
///      emission produces <c>Dep.Container.AlertType</c> — which C# resolves
///      to the <em>property</em>, not the type — yielding CS0426.
///
/// These tests lock contract (2): applying the rename pass to a dep module's
/// <c>ModuleDecl</c> propagates the renamed C# name to the shared
/// <c>TypeDatabase</c> so cross-module lookups see it.
/// </summary>
public class NestedTypeRenameTests
{
    [Fact]
    public void PrecomputeNestedTypeRenames_AppliedToDepModule_RenamesDepTypeRecordForCrossModuleLookup()
    {
        // Reproduces the cross-module nested-type rename shape exactly.
        //
        // Dep module "DepLib" declares:
        //   struct Container {
        //     var alertType: AlertType   // Swift property
        //     enum AlertType { ... }     // nested type — PascalCases to same name as property
        //   }
        //
        // Consumer module "ConsumerLib" references Container.AlertType. After
        // applying the rename pass on the dep ModuleDecl, the dep TypeRecord for
        // DepLib.Container.AlertType must show C# name "Container.AlertTypeKind"
        // so the consumer's emitter resolves the cross-module reference correctly.
        // (AlertType is a Swift enum, so the kind-aware suffix is "Kind"; it does not
        // already end in "Kind", so no numeric fallback is needed.)
        var typeDatabase = new TypeDatabase();

        // --- Dep module setup ---
        var depModule = new ModuleTypeDatabase("DepLib", "/tmp/DepLib.dylib");

        var depContainerSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Container");
        depModule.RegisterType(depContainerSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Container"),
            SwiftTypeName = depContainerSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var depAlertTypeSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Container.AlertType");
        depModule.RegisterType(depAlertTypeSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Container.AlertType"),
            SwiftTypeName = depAlertTypeSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Enum
        });

        typeDatabase.AddModuleDatabase(depModule);

        // --- Consumer module setup (empty — we just need it in the same TypeDatabase) ---
        var consumerModule = new ModuleTypeDatabase("ConsumerLib", "/tmp/ConsumerLib.dylib");
        typeDatabase.AddModuleDatabase(consumerModule);

        // --- Dep ModuleDecl with the collision shape ---
        var depModuleDecl = new ModuleDecl
        {
            Name = "DepLib",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var depAlertTypeDecl = CreateEnumDecl("AlertType", depAlertTypeSwiftName, depModuleDecl);
        var depContainerDecl = new StructDecl
        {
            Name = "Container",
            SwiftTypeName = depContainerSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "alertType",
                    // Property type IS the nested type → rename type, not property.
                    SwiftTypeSpec = new NamedTypeSpec("DepLib.Container")
                    {
                        InnerType = new NamedTypeSpec("AlertType")
                    },
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = depModuleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { depAlertTypeDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = depModuleDecl,
            ModuleDecl = depModuleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        depAlertTypeDecl.ParentDecl = depContainerDecl;
        depModuleDecl.Types.Add(depContainerDecl);

        // Sanity: before the rename pass, the dep TypeRecord shows the un-renamed name.
        // This is the un-fixed state — without the Program.cs dep-loading rename call,
        // the consumer's emitter would resolve cross-module references against this
        // un-renamed name and produce CS0426.
        Assert.True(typeDatabase.TryGetTypeRecord(depAlertTypeSwiftName, out var preRenameRecord));
        Assert.Equal("Container.AlertType", preRenameRecord!.CSharpTypeName.Name);

        // Act: simulate Program.cs's dep-loading rename call.
        NameProvider.PrecomputeNestedTypeRenames(depModuleDecl, typeDatabase);

        // Assert: the dep TypeRecord now carries the renamed C# leaf name.
        // A cross-module lookup from the consumer's emitter will read this record
        // and produce DepLib.Container.AlertTypeKind in the generated C#.
        Assert.True(typeDatabase.TryGetTypeRecord(depAlertTypeSwiftName, out var renamedRecord));
        Assert.Equal("Container.AlertTypeKind", renamedRecord!.CSharpTypeName.Name);
        Assert.Equal("DepLib", renamedRecord.CSharpTypeName.Namespace);
    }

    [Fact]
    public void PrecomputeNestedTypeRenames_AppliedToDepModuleWithoutCollision_LeavesTypeRecordUnchanged()
    {
        // A dep module with a nested type but no colliding property must not have
        // any rename applied — the pass is a no-op. Guards against accidental
        // over-renaming when the fix runs on every dep module.
        var typeDatabase = new TypeDatabase();
        var depModule = new ModuleTypeDatabase("DepLib", "/tmp/DepLib.dylib");

        var containerSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Container");
        depModule.RegisterType(containerSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Container"),
            SwiftTypeName = containerSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var settingsSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Container.Settings");
        depModule.RegisterType(settingsSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Container.Settings"),
            SwiftTypeName = settingsSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(depModule);

        var depModuleDecl = new ModuleDecl
        {
            Name = "DepLib",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var settingsDecl = CreateStructDecl("Settings", settingsSwiftName, depModuleDecl);
        var containerDecl = new StructDecl
        {
            Name = "Container",
            SwiftTypeName = containerSwiftName,
            MangledName = "$sN",
            // Property name "name" does NOT collide with nested type "Settings".
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
                    ModuleDecl = depModuleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { settingsDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = depModuleDecl,
            ModuleDecl = depModuleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        settingsDecl.ParentDecl = containerDecl;
        depModuleDecl.Types.Add(containerDecl);

        NameProvider.PrecomputeNestedTypeRenames(depModuleDecl, typeDatabase);

        Assert.True(typeDatabase.TryGetTypeRecord(settingsSwiftName, out var settingsRecord));
        Assert.Equal("Container.Settings", settingsRecord!.CSharpTypeName.Name);
    }

    [Fact]
    public void PrecomputeNestedTypeRenames_StructParentWithTypeSuffixChild_UsesInfoSuffixWithoutNumericBump()
    {
        // Reproduces a struct where a stored property name collides with a sibling nested type,
        // and that sibling itself contains a child whose leaf ends in "Type":
        //   struct Card {
        //     var wallet: Wallet      // collides with sibling type Wallet
        //     struct Wallet {
        //       enum WalletType { ... }
        //     }
        //   }
        // Under the kind-aware scheme, Wallet is a struct → suffix "Info" → "WalletInfo",
        // which does NOT collide with the "WalletType" child. So no numeric fallback fires
        // and the child cascades cleanly to "WalletInfo.WalletType". (Under the older "Type"
        // suffix scheme this shape DID collide — Wallet → "WalletType" clashed with the child —
        // and needed a stuttering-avoidance numeric bump to "WalletType2". The kind-aware
        // suffix sidesteps that collision entirely.)
        var typeDatabase = new TypeDatabase();
        var depModule = new ModuleTypeDatabase("DepLib", "/tmp/DepLib.dylib");

        var cardSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Card");
        depModule.RegisterType(cardSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Card"),
            SwiftTypeName = cardSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var walletSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Card.Wallet");
        depModule.RegisterType(walletSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Card.Wallet"),
            SwiftTypeName = walletSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var walletTypeSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Card.Wallet.WalletType");
        depModule.RegisterType(walletTypeSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Card.Wallet.WalletType"),
            SwiftTypeName = walletTypeSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            Kind = TypeRecordKind.Enum
        });

        typeDatabase.AddModuleDatabase(depModule);

        var depModuleDecl = new ModuleDecl
        {
            Name = "DepLib",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var walletTypeDecl = CreateEnumDecl("WalletType", walletTypeSwiftName, depModuleDecl);
        var walletDecl = new StructDecl
        {
            Name = "Wallet",
            SwiftTypeName = walletSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { walletTypeDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = depModuleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        walletTypeDecl.ParentDecl = walletDecl;

        var cardDecl = new StructDecl
        {
            Name = "Card",
            SwiftTypeName = cardSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "wallet",
                    SwiftTypeSpec = new NamedTypeSpec("DepLib.Card")
                    {
                        InnerType = new NamedTypeSpec("Wallet")
                    },
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = depModuleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { walletDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = depModuleDecl,
            ModuleDecl = depModuleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        walletDecl.ParentDecl = cardDecl;
        depModuleDecl.Types.Add(cardDecl);

        NameProvider.PrecomputeNestedTypeRenames(depModuleDecl, typeDatabase);

        // Wallet (struct) is renamed with the "Info" suffix; "WalletInfo" does not collide
        // with the "WalletType" child, so no numeric fallback is needed.
        Assert.True(typeDatabase.TryGetTypeRecord(walletSwiftName, out var walletRecord));
        Assert.Equal("Card.WalletInfo", walletRecord!.CSharpTypeName.Name);
        // The child enum's path must cascade with the renamed parent.
        Assert.True(typeDatabase.TryGetTypeRecord(walletTypeSwiftName, out var innerRecord));
        Assert.Equal("Card.WalletInfo.WalletType", innerRecord!.CSharpTypeName.Name);
    }

    [Fact]
    public void PrecomputeNestedTypeRenames_EnumChildLeafAlreadyEndsInKind_DoesNotStutterAndFallsBackToNumeric()
    {
        // A struct whose stored property collides with a sibling ENUM whose leaf name already
        // ends in the kind-aware suffix "Kind":
        //   struct Host {
        //     var tokenKind: TokenKind   // property projects to C# "TokenKind"
        //     enum TokenKind { ... }     // sibling enum, same C# leaf → collision
        //   }
        // Enum → suffix "Kind", but "TokenKind" already ends in "Kind", so the anti-stutter guard
        // must NOT produce "TokenKindKind". The base leaf stays "TokenKind", which equals the
        // colliding property name, so the numeric fallback disambiguates to "TokenKind2".
        var typeDatabase = new TypeDatabase();
        var depModule = new ModuleTypeDatabase("DepLib", "/tmp/DepLib.dylib");

        var hostSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Host");
        depModule.RegisterType(hostSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Host"),
            SwiftTypeName = hostSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var tokenKindSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Host.TokenKind");
        depModule.RegisterType(tokenKindSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Host.TokenKind"),
            SwiftTypeName = tokenKindSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            Kind = TypeRecordKind.Enum
        });

        typeDatabase.AddModuleDatabase(depModule);

        var depModuleDecl = new ModuleDecl
        {
            Name = "DepLib",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var tokenKindDecl = CreateEnumDecl("TokenKind", tokenKindSwiftName, depModuleDecl);
        var hostDecl = new StructDecl
        {
            Name = "Host",
            SwiftTypeName = hostSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "tokenKind",
                    SwiftTypeSpec = new NamedTypeSpec("DepLib.Host")
                    {
                        InnerType = new NamedTypeSpec("TokenKind")
                    },
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = depModuleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { tokenKindDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = depModuleDecl,
            ModuleDecl = depModuleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        tokenKindDecl.ParentDecl = hostDecl;
        depModuleDecl.Types.Add(hostDecl);

        NameProvider.PrecomputeNestedTypeRenames(depModuleDecl, typeDatabase);

        // Anti-stutter: NOT "TokenKindKind". The base leaf "TokenKind" equals the colliding
        // property name, so the numeric fallback lands on "TokenKind2".
        Assert.True(typeDatabase.TryGetTypeRecord(tokenKindSwiftName, out var renamedRecord));
        Assert.Equal("Host.TokenKind2", renamedRecord!.CSharpTypeName.Name);
    }

    [Fact]
    public void PrecomputeNestedTypeRenames_TargetHasOwnChildSharingSemanticSuffixName_AppendsNumericSuffix()
    {
        // Reproduces a struct where the kind-aware rename target collides with the renamed
        // type's OWN child:
        //   struct Card {
        //     var wallet: Wallet      // collides with sibling type Wallet
        //     struct Wallet {
        //       struct WalletInfo { ... } // same name the "Info" suffix would produce
        //     }
        //   }
        // Wallet (struct) → "WalletInfo", but that leaf is already claimed by Wallet's own
        // child, so the numeric fallback must pick "WalletInfo2". Without this guard the C#
        // emission trips CS0542 ("member names cannot be the same as their enclosing type").
        var typeDatabase = new TypeDatabase();
        var depModule = new ModuleTypeDatabase("DepLib", "/tmp/DepLib.dylib");

        var cardSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Card");
        depModule.RegisterType(cardSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Card"),
            SwiftTypeName = cardSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var walletSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Card.Wallet");
        depModule.RegisterType(walletSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Card.Wallet"),
            SwiftTypeName = walletSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var walletInfoSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Card.Wallet.WalletInfo");
        depModule.RegisterType(walletInfoSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Card.Wallet.WalletInfo"),
            SwiftTypeName = walletInfoSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(depModule);

        var depModuleDecl = new ModuleDecl
        {
            Name = "DepLib",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var walletInfoDecl = CreateStructDecl("WalletInfo", walletInfoSwiftName, depModuleDecl);
        var walletDecl = new StructDecl
        {
            Name = "Wallet",
            SwiftTypeName = walletSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { walletInfoDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = depModuleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        walletInfoDecl.ParentDecl = walletDecl;

        var cardDecl = new StructDecl
        {
            Name = "Card",
            SwiftTypeName = cardSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "wallet",
                    SwiftTypeSpec = new NamedTypeSpec("DepLib.Card")
                    {
                        InnerType = new NamedTypeSpec("Wallet")
                    },
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = depModuleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { walletDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = depModuleDecl,
            ModuleDecl = depModuleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        walletDecl.ParentDecl = cardDecl;
        depModuleDecl.Types.Add(cardDecl);

        NameProvider.PrecomputeNestedTypeRenames(depModuleDecl, typeDatabase);

        // Wallet must NOT be renamed to "WalletInfo" (Wallet has a child with that name).
        Assert.True(typeDatabase.TryGetTypeRecord(walletSwiftName, out var walletRecord));
        Assert.Equal("Card.WalletInfo2", walletRecord!.CSharpTypeName.Name);
        // The child's path must cascade with the renamed parent.
        Assert.True(typeDatabase.TryGetTypeRecord(walletInfoSwiftName, out var innerRecord));
        Assert.Equal("Card.WalletInfo2.WalletInfo", innerRecord!.CSharpTypeName.Name);
    }

    [Fact]
    public void PrecomputeNestedTypeRenames_LowercaseSiblingSharesEmittedLeaf_AppendsNumericSuffix()
    {
        // Reproduces a struct whose kind-aware rename target collides with a SIBLING nested type
        // that has a lowercase Swift identifier (legal Swift) whose emitted C# leaf is the
        // PascalCase of that identifier:
        //   struct Host {
        //     var entry: Entry        // collides with sibling type Entry → rename
        //     struct Entry {}         // struct → "Info" suffix → base leaf "EntryInfo"
        //     struct entryInfo {}     // lowercase Swift type → emits as C# "EntryInfo"
        //   }
        // Entry → "EntryInfo" would collide with entryInfo's emitted "EntryInfo" (CS0102), so the
        // numeric fallback must land on "EntryInfo2". The collision-guard sets are seeded with the
        // emitted PascalCase leaf (not the raw Swift name) — otherwise raw "entryInfo" would not
        // match the "EntryInfo" rename target and the duplicate would slip through.
        var typeDatabase = new TypeDatabase();
        var depModule = new ModuleTypeDatabase("DepLib", "/tmp/DepLib.dylib");

        var hostSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Host");
        depModule.RegisterType(hostSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Host"),
            SwiftTypeName = hostSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var entrySwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Host.Entry");
        depModule.RegisterType(entrySwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Host.Entry"),
            SwiftTypeName = entrySwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        // Lowercase Swift type identifier; real registration PascalCases the emitted leaf.
        var entryInfoSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Host.entryInfo");
        depModule.RegisterType(entryInfoSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Host.EntryInfo"),
            SwiftTypeName = entryInfoSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(depModule);

        var depModuleDecl = new ModuleDecl
        {
            Name = "DepLib",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var entryDecl = CreateStructDecl("Entry", entrySwiftName, depModuleDecl);
        var entryInfoDecl = CreateStructDecl("entryInfo", entryInfoSwiftName, depModuleDecl);
        var hostDecl = new StructDecl
        {
            Name = "Host",
            SwiftTypeName = hostSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "entry",
                    SwiftTypeSpec = new NamedTypeSpec("DepLib.Host")
                    {
                        InnerType = new NamedTypeSpec("Entry")
                    },
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = depModuleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { entryDecl, entryInfoDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = depModuleDecl,
            ModuleDecl = depModuleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        entryDecl.ParentDecl = hostDecl;
        entryInfoDecl.ParentDecl = hostDecl;
        depModuleDecl.Types.Add(hostDecl);

        NameProvider.PrecomputeNestedTypeRenames(depModuleDecl, typeDatabase);

        // Entry (struct) → "EntryInfo" collides with entryInfo's emitted leaf "EntryInfo",
        // so the numeric fallback lands on "EntryInfo2".
        Assert.True(typeDatabase.TryGetTypeRecord(entrySwiftName, out var entryRecord));
        Assert.Equal("Host.EntryInfo2", entryRecord!.CSharpTypeName.Name);
        // The lowercase sibling is not renamed; it keeps its emitted "EntryInfo" leaf.
        Assert.True(typeDatabase.TryGetTypeRecord(entryInfoSwiftName, out var siblingRecord));
        Assert.Equal("Host.EntryInfo", siblingRecord!.CSharpTypeName.Name);
    }

    [Fact]
    public void GetEmittedNestedTypeLeafName_ReturnsRenamedLeafForRenamedType_AndPascalCaseForUnrenamed()
    {
        // Directly locks the helper the emitter's method/nested-type collision sets read: after
        // the rename pass a renamed nested type reports its RENAMED emitted leaf, while an
        // unrenamed sibling reports the plain PascalCase of its Swift name. Reserving the wrong
        // one lets a colliding sibling method emit under the renamed type's name → CS0102.
        //   struct Box {
        //     var item: Item     // collides → Item renamed to "ItemInfo"
        //     struct Item {}
        //     struct Plain {}    // never renamed
        //   }
        var typeDatabase = new TypeDatabase();
        var depModule = new ModuleTypeDatabase("DepLib", "/tmp/DepLib.dylib");

        var boxSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Box");
        depModule.RegisterType(boxSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Box"),
            SwiftTypeName = boxSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var itemSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Box.Item");
        depModule.RegisterType(itemSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Box.Item"),
            SwiftTypeName = itemSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var plainSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Box.Plain");
        depModule.RegisterType(plainSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Box.Plain"),
            SwiftTypeName = plainSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(depModule);

        var depModuleDecl = new ModuleDecl
        {
            Name = "DepLib",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var itemDecl = CreateStructDecl("Item", itemSwiftName, depModuleDecl);
        var plainDecl = CreateStructDecl("Plain", plainSwiftName, depModuleDecl);
        var boxDecl = new StructDecl
        {
            Name = "Box",
            SwiftTypeName = boxSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "item",
                    SwiftTypeSpec = new NamedTypeSpec("DepLib.Box")
                    {
                        InnerType = new NamedTypeSpec("Item")
                    },
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = depModuleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { itemDecl, plainDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = depModuleDecl,
            ModuleDecl = depModuleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        itemDecl.ParentDecl = boxDecl;
        plainDecl.ParentDecl = boxDecl;
        depModuleDecl.Types.Add(boxDecl);

        NameProvider.PrecomputeNestedTypeRenames(depModuleDecl, typeDatabase);

        // Renamed nested type → renamed emitted leaf.
        Assert.Equal("ItemInfo", NameProvider.GetEmittedNestedTypeLeafName(itemDecl, typeDatabase));
        // Unrenamed sibling → plain PascalCase of the Swift name.
        Assert.Equal("Plain", NameProvider.GetEmittedNestedTypeLeafName(plainDecl, typeDatabase));
        // Null database → always the PascalCase fallback (no record lookup).
        Assert.Equal("Item", NameProvider.GetEmittedNestedTypeLeafName(itemDecl, null));
    }

    [Fact]
    public void PrecomputeNestedTypeRenames_DatabaseDoesNotImplementApplyEmissionResult_ThrowsRatherThanSilentlySwallowingRename()
    {
        // Fail-safe guard: a type database that drives emission but leaves ApplyEmissionResult at the
        // interface default must FAIL LOUDLY when the rename pass stamps a nested-type rename — never
        // silently swallow it. A swallowed stamp leaves emitted code referencing a name the database
        // never recorded (the rest of emission has already committed to the rename by this point).
        //
        // Same Container/alertType/AlertType collision shape as the cross-module test above, but the
        // rename pass runs against a database whose ApplyEmissionResult is the (throwing) DIM default.
        var inner = new TypeDatabase();
        var depModule = new ModuleTypeDatabase("DepLib", "/tmp/DepLib.dylib");

        var containerSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Container");
        depModule.RegisterType(containerSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Container"),
            SwiftTypeName = containerSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var alertTypeSwiftName = SwiftTypeName.FromModuleQualifiedName("DepLib.Container.AlertType");
        depModule.RegisterType(alertTypeSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepLib", "Container.AlertType"),
            SwiftTypeName = alertTypeSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        inner.AddModuleDatabase(depModule);

        var depModuleDecl = new ModuleDecl
        {
            Name = "DepLib",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var alertTypeDecl = CreateStructDecl("AlertType", alertTypeSwiftName, depModuleDecl);
        var containerDecl = new StructDecl
        {
            Name = "Container",
            SwiftTypeName = containerSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "alertType",
                    SwiftTypeSpec = new NamedTypeSpec("DepLib.Container")
                    {
                        InnerType = new NamedTypeSpec("AlertType")
                    },
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = depModuleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { alertTypeDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = depModuleDecl,
            ModuleDecl = depModuleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        alertTypeDecl.ParentDecl = containerDecl;
        depModuleDecl.Types.Add(containerDecl);

        var nonStamping = new NonStampingTypeDatabase(inner);

        var ex = Assert.Throws<NotImplementedException>(
            () => NameProvider.PrecomputeNestedTypeRenames(depModuleDecl, nonStamping));
        Assert.Contains("ApplyEmissionResult", ex.Message);
    }

    /// <summary>
    /// An <see cref="ITypeDatabase"/> that delegates every member to a real
    /// <see cref="TypeDatabase"/> EXCEPT <see cref="ITypeDatabase.ApplyEmissionResult"/>, which it
    /// leaves to the interface's (throwing) default. Models a database that drives emission but never
    /// wired the emission-stamp mutation — the exact shape the fail-safe default catches.
    /// </summary>
    private sealed class NonStampingTypeDatabase : ITypeDatabase
    {
        private readonly TypeDatabase _inner;
        public NonStampingTypeDatabase(TypeDatabase inner) => _inner = inner;

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _inner.IsTypeProcessed(swiftTypeName);
        public bool IsTypeRegistered(SwiftTypeName swiftTypeName) => _inner.IsTypeRegistered(swiftTypeName);
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
            => _inner.TryGetTypeRecord(swiftTypeName, out record);
        public bool TryGetTypeRecordWithoutSupplement(SwiftTypeName swiftTypeName, out TypeRecord record)
            => _inner.TryGetTypeRecordWithoutSupplement(swiftTypeName, out record);
        public string GetLibraryPath(string moduleName) => _inner.GetLibraryPath(moduleName);
        public string AsyncLibraryName => _inner.AsyncLibraryName;
        // GenerationMode is a pure interface default (computed from AsyncLibraryName, which this
        // double supplies) — inherited rather than delegated, since the concrete TypeDatabase does
        // not expose it as its own member.
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) => _inner.UpdateTypeRecord(name, record);
        public void Freeze() => _inner.Freeze();
        public IReadOnlyCollection<(string Namespace, string ProxyName)> GetCrossModuleSuppressedProxyClassNames()
            => _inner.GetCrossModuleSuppressedProxyClassNames();
        public void AddDependencyModuleDecl(ModuleDecl moduleDecl) => _inner.AddDependencyModuleDecl(moduleDecl);
        public IReadOnlyList<ModuleDecl> GetDependencyModuleDecls() => _inner.GetDependencyModuleDecls();
        public void RegisterStrippedConformance(SwiftTypeName concreteType, SwiftTypeName protocolName)
            => _inner.RegisterStrippedConformance(concreteType, protocolName);
        public bool HasStrippedConformance(SwiftTypeName concreteType, SwiftTypeName protocolName)
            => _inner.HasStrippedConformance(concreteType, protocolName);
        // ApplyEmissionResult deliberately NOT overridden → inherits the throwing interface default.
    }

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
}
