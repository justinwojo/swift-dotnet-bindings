// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Cross-module propagation of the property/nested-type rename.
///
/// The producer's <see cref="NameProvider.PrecomputeNestedTypeRenames"/> pass
/// detects a parent type that has both a nested type and a property whose
/// PascalCased names collide and renames the nested type with a "Type" suffix.
/// The rename mutates the nested type's <c>TypeRecord.CSharpTypeName</c> in
/// the producer's <c>ModuleTypeDatabase</c>.
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
        // Reproduces the BlinkIDUX → BlinkID shape exactly.
        //
        // Dep module "DepLib" declares:
        //   struct Container {
        //     var alertType: AlertType   // Swift property
        //     enum AlertType { ... }     // nested type — PascalCases to same name as property
        //   }
        //
        // Consumer module "ConsumerLib" references Container.AlertType. After
        // applying the rename pass on the dep ModuleDecl, the dep TypeRecord for
        // DepLib.Container.AlertType must show C# name "Container.AlertTypeType"
        // so the consumer's emitter resolves the cross-module reference correctly.
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
            Kind = TypeRecordKind.Struct
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

        var depAlertTypeDecl = CreateStructDecl("AlertType", depAlertTypeSwiftName, depModuleDecl);
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
        // and produce DepLib.Container.AlertTypeType in the generated C#.
        Assert.True(typeDatabase.TryGetTypeRecord(depAlertTypeSwiftName, out var renamedRecord));
        Assert.Equal("Container.AlertTypeType", renamedRecord!.CSharpTypeName.Name);
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
    public void PrecomputeNestedTypeRenames_TargetHasOwnChildWithSameName_AppendsExtraTypeSuffix()
    {
        // Reproduces StripeApplePay's Card.Wallet shape:
        //   struct Card {
        //     var wallet: Wallet      // collides with sibling type Wallet
        //     struct Wallet {
        //       enum WalletType { ... } // would collide with renamed `Wallet → WalletType`
        //     }
        //   }
        // The rename must skip "WalletType" (claimed by Wallet's own child) and pick
        // "WalletTypeType" instead. Without this guard, the C# emission trips CS0542
        // ("member names cannot be the same as their enclosing type").
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

        var walletTypeDecl = CreateStructDecl("WalletType", walletTypeSwiftName, depModuleDecl);
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

        // Wallet must NOT be renamed to "WalletType" (Wallet has a child enum with that name).
        Assert.True(typeDatabase.TryGetTypeRecord(walletSwiftName, out var walletRecord));
        Assert.Equal("Card.WalletTypeType", walletRecord!.CSharpTypeName.Name);
        // The child enum's path must cascade with the renamed parent.
        Assert.True(typeDatabase.TryGetTypeRecord(walletTypeSwiftName, out var innerRecord));
        Assert.Equal("Card.WalletTypeType.WalletType", innerRecord!.CSharpTypeName.Name);
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
}
