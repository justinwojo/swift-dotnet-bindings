// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Withdrawal-plane completeness. The ingestion-quarantine closure hands emission a poison list,
/// and the type emitter honours it — but several emitters are SEPARATE planes that walk the RAW
/// module tree (or the type database) and name types on their own authority. Every such plane has
/// to consult the withdrawal set, or the binding ships C# that references a type nothing declared:
/// a compile error in the consumer's build, produced by a generator that exited 0.
///
/// Each test here drives one plane twice — once with an empty poison list (the type is named) and
/// once with the type withdrawn (the plane must decline) — so a plane that stops consulting the
/// set turns exactly one case red rather than silently regressing.
/// </summary>
public class IngestionWithdrawalPlaneTests
{
    private const string Module = "PlaneModule";

    /// <summary>
    /// The typed-error registry precomputes from the raw module tree, so a withdrawn concrete
    /// Error-conforming type stays registered unless the registry consults the withdrawal set
    /// itself. A registered id makes both halves of the cascade name the type: the Swift
    /// <c>as? Module.Fault</c> and the C# <c>SwiftException&lt;Module.Fault&gt;</c>. Only a
    /// concrete Error-conforming type reaches this plane, which is why a plain withdrawn value
    /// type cannot exercise it.
    /// </summary>
    [Fact]
    public void ErrorRegistry_WithdrawnErrorConformingType_NotRegistered()
    {
        var withdrawn = MakeErrorEnum("WithdrawnFault");
        var healthy = MakeErrorEnum("HealthyFault");
        var module = MakeModule(withdrawn, healthy);

        // Control: nothing withdrawn — both types are registered.
        var openContext = new ModuleEmissionContext();
        using (BeginAttempt())
        {
            ErrorEnumRegistryEmitter.Precompute(module, openContext);
        }
        Assert.True(openContext.TryGetErrorTypeId($"{Module}.WithdrawnFault", out _));
        Assert.True(openContext.TryGetErrorTypeId($"{Module}.HealthyFault", out _));

        // Withdrawn: the registry must drop the withdrawn type and keep its healthy sibling.
        var poisonedContext = new ModuleEmissionContext();
        using (BeginAttempt(withdrawn))
        {
            ErrorEnumRegistryEmitter.Precompute(module, poisonedContext);
        }
        Assert.False(poisonedContext.TryGetErrorTypeId($"{Module}.WithdrawnFault", out _));
        Assert.True(poisonedContext.TryGetErrorTypeId($"{Module}.HealthyFault", out _));
    }

    /// <summary>
    /// The theme bridge walks the raw module classes and emits <c>public partial class {Name}</c>
    /// plus accessors for each one it accepts. For a withdrawn class that partial half has no other
    /// half — the binding never declares the type.
    /// </summary>
    [Fact]
    public void ThemeBridge_WithdrawnClass_NotDetected()
    {
        var withdrawn = MakeThemeClass("WithdrawnTheme");
        var healthy = MakeThemeClass("HealthyTheme");
        var module = MakeModule(withdrawn, healthy);

        using (BeginAttempt())
        {
            var detected = ThemeBridgeEmitter.DetectThemeBridgeableTypes(module);
            Assert.Contains(detected, d => d.ClassName == "WithdrawnTheme");
            Assert.Contains(detected, d => d.ClassName == "HealthyTheme");
        }

        using (BeginAttempt(withdrawn))
        {
            var detected = ThemeBridgeEmitter.DetectThemeBridgeableTypes(module);
            Assert.DoesNotContain(detected, d => d.ClassName == "WithdrawnTheme");
            Assert.Contains(detected, d => d.ClassName == "HealthyTheme");
        }
    }

    /// <summary>
    /// The Swift type-ownership manifest is the ObjC-companion dedup oracle: an entry claims the
    /// ObjC runtime name for the Swift side and the companion drops its own declaration of that
    /// name. Claiming the name for a withdrawn type loses BOTH halves. This plane runs after
    /// emission returns — outside the ambient emission attempt — so it is told the withdrawal set
    /// explicitly rather than reading the poison list.
    /// </summary>
    [Fact]
    public void OwnershipManifest_WithdrawnType_DoesNotClaimTheObjCRuntimeName()
    {
        var withdrawn = MakeThemeClass("WithdrawnOwner");
        var healthy = MakeThemeClass("HealthyOwner");
        var module = MakeModule(withdrawn, healthy);

        var openManifest = SwiftTypeOwnershipManifestEmitter.Build(module);
        Assert.Contains(openManifest.Types, t => t.SwiftName == "WithdrawnOwner");
        Assert.Contains(openManifest.Types, t => t.SwiftName == "HealthyOwner");

        var withdrawnNames = new HashSet<string>(StringComparer.Ordinal) { $"{Module}.WithdrawnOwner" };
        var degradedManifest = SwiftTypeOwnershipManifestEmitter.Build(module, withdrawnNames);
        Assert.DoesNotContain(degradedManifest.Types, t => t.SwiftName == "WithdrawnOwner");
        Assert.Contains(degradedManifest.Types, t => t.SwiftName == "HealthyOwner");
    }

    /// <summary>
    /// The ownership manifest's withdrawal set must carry EVERY whole-type refusal, not just the
    /// ingestion ones. A containment denial and a verify-recover withdrawal reach Gate 0 through
    /// different causes but land in the report identically, and the ingestion closure knows nothing
    /// about either — so a set derived from the ingestion closure alone leaves such a type still
    /// claiming its ObjC runtime name, which is the shape that loses the type on both planes.
    /// </summary>
    [Fact]
    public void WithdrawalSet_NonIngestionEmitterFault_ReachesTheOwnershipManifest()
    {
        var deniedByContainment = MakeThemeClass("ContainmentDenied");
        var healthy = MakeThemeClass("HealthyOwner");
        var module = MakeModule(deniedByContainment, healthy);

        // A report carrying one type-scope EmitterFault skip — and NOTHING from the ingestion
        // closure, which is what makes this the non-ingestion case.
        var report = new BindingReport { ModuleName = Module };
        report.SkippedItems.Add(new SkippedItem
        {
            Kind = BindingItemKind.Type,
            Name = deniedByContainment.Name,
            Reason = SkipReason.EmitterFault,
            DeclId = DeclIdFactory.ForType(deniedByContainment).Canonical,
        });

        var withdrawn = PostEmissionWithdrawalSet.Build(report, ingestionWithdrawnTypeNames: null);
        Assert.Contains($"{Module}.ContainmentDenied", withdrawn);
        Assert.DoesNotContain($"{Module}.HealthyOwner", withdrawn);

        var manifest = SwiftTypeOwnershipManifestEmitter.Build(module, withdrawn);
        Assert.DoesNotContain(manifest.Types, t => t.SwiftName == "ContainmentDenied");
        Assert.Contains(manifest.Types, t => t.SwiftName == "HealthyOwner");
    }

    /// <summary>
    /// Skips that are NOT whole-type refusals must not enter the set: an Apple-supplement-owned type
    /// or a SwiftUI View IS declared, just elsewhere, so withdrawing its ObjC runtime name would tell
    /// the companion to keep a declaration the Swift side really does own.
    /// </summary>
    [Fact]
    public void WithdrawalSet_NonEmitterFaultSkip_IsNotWithdrawn()
    {
        var view = MakeThemeClass("SomeView");
        var report = new BindingReport { ModuleName = Module };
        report.SkippedItems.Add(new SkippedItem
        {
            Kind = BindingItemKind.Type,
            Name = view.Name,
            Reason = SkipReason.SwiftUIView,
            DeclId = DeclIdFactory.ForType(view).Canonical,
        });

        Assert.Empty(PostEmissionWithdrawalSet.Build(report, ingestionWithdrawnTypeNames: null));
    }

    /// <summary>
    /// The SwiftUI bridge maps init parameters off the RAW module tree, so it resolves a withdrawn
    /// type just fine and would emit a bridge naming a C# class the binding never declares. A NESTED
    /// type is the case the resolution has to get right: the parameter names the full dotted path
    /// (<c>Module.Outer.Inner</c>) while a TypeDecl carries only its LEAF name, so a lookup that
    /// compares the dotted remainder against the module's top-level type names never matches and the
    /// withdrawal reads as absent.
    /// </summary>
    [Fact]
    public void SwiftUIBridge_WithdrawnNestedType_IsRefused()
    {
        var inner = MakeThemeClass("Inner");
        var outer = MakeThemeClass("Outer");
        var module = MakeModule(outer);
        // Nest AFTER MakeModule so only Outer is a top-level type; Inner is reachable solely by
        // walking Outer.Types, which is the path under test.
        inner.ParentDecl = outer;
        inner.ModuleDecl = module;
        outer.Types.Add(inner);

        var context = new BridgeContext(ModuleDecl: module);
        var nestedSpec = new NamedTypeSpec($"{Module}.Outer.Inner");

        // Control: nothing withdrawn — the nested type is a normal, nameable type.
        using (BeginAttempt())
        {
            Assert.False(SwiftUIBridgeEmitter.ReachesWithdrawnModuleType(nestedSpec, context));
        }

        // The nested type itself withdrawn: the bridge must refuse it.
        using (BeginAttempt(inner))
        {
            Assert.True(SwiftUIBridgeEmitter.ReachesWithdrawnModuleType(nestedSpec, context));
        }

        // The CONTAINER withdrawn: Inner is declared inside a C# class the binding never emits, so
        // it is just as unreachable as a directly-withdrawn leaf.
        using (BeginAttempt(outer))
        {
            Assert.True(SwiftUIBridgeEmitter.ReachesWithdrawnModuleType(nestedSpec, context));
        }
    }

    /// <summary>
    /// A whole-type refusal is recorded against the OUTER type only, but a nested type has no
    /// declaration without its container — it is emitted INSIDE the C# class the binding never
    /// wrote. Both post-emission planes name nested types on their own authority (the ownership
    /// manifest recurses through <c>type.Types</c>, the module database holds a flat record per
    /// nested type), so an exact-name-only withdrawal set leaves both advertising
    /// <c>M.Outer.Inner</c> after <c>M.Outer</c> was withdrawn. Closing the set over descendants
    /// once, in the shared builder, is what makes the two planes agree by construction.
    ///
    /// The unrelated sibling <c>OuterOther</c> is the discrimination case: its name shares
    /// <c>M.Outer</c> as a string prefix, so a prefix-matching set would swallow a healthy type.
    /// </summary>
    [Fact]
    public async Task WithdrawalSet_OuterTypeFault_CascadesToNestedDescendants()
    {
        var outer = MakeThemeClass("Outer");
        var sibling = MakeThemeClass("OuterOther");
        var module = MakeModule(outer, sibling);

        // Nest AFTER MakeModule so Inner is reachable only by walking Outer.Types, and give it the
        // dotted qualified name a real nested type carries.
        var inner = MakeThemeClass("Inner");
        inner.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Outer.Inner");
        inner.ParentDecl = outer;
        inner.ModuleDecl = module;
        outer.Types.Add(inner);

        var report = new BindingReport { ModuleName = Module };
        report.SkippedItems.Add(new SkippedItem
        {
            Kind = BindingItemKind.Type,
            Name = outer.Name,
            Reason = SkipReason.EmitterFault,
            DeclId = DeclIdFactory.ForType(outer).Canonical,
        });

        var withdrawn = PostEmissionWithdrawalSet.Build(report, ingestionWithdrawnTypeNames: null, moduleDecl: module);

        Assert.Contains($"{Module}.Outer", withdrawn);
        Assert.Contains($"{Module}.Outer.Inner", withdrawn);
        Assert.DoesNotContain($"{Module}.OuterOther", withdrawn);

        // Plane 1 — the ownership manifest must stop claiming the nested type's ObjC runtime name.
        var manifest = SwiftTypeOwnershipManifestEmitter.Build(module, withdrawn);
        Assert.DoesNotContain(manifest.Types, t => t.SwiftName == "Outer");
        Assert.DoesNotContain(manifest.Types, t => t.SwiftName == "Inner");
        Assert.Contains(manifest.Types, t => t.SwiftName == "OuterOther");

        // Plane 2 — the serialized module database must not advertise the nested record either.
        var dir = Directory.CreateTempSubdirectory("withdrawal-cascade").FullName;
        try
        {
            var moduleDb = new ModuleTypeDatabase(Module, $"/fake/{Module}.dylib");
            RegisterClassRecord(moduleDb, $"{Module}.Outer", "Outer");
            RegisterClassRecord(moduleDb, $"{Module}.Outer.Inner", "Inner");
            RegisterClassRecord(moduleDb, $"{Module}.OuterOther", "OuterOther");

            var path = ModuleDatabaseEmitter.Emit(
                moduleDb, dir, NullLogger.Instance, withdrawnTypeNames: withdrawn);

            Assert.NotNull(path);

            // Assert through the reader the consumer actually uses, not the file's text: the
            // question is which types the emitted database still ADVERTISES.
            var loaded = new TypeDatabase();
            await loaded.LoadModuleDatabaseFromFile(path!);
            Assert.False(loaded.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName($"{Module}.Outer"), out _));
            Assert.False(loaded.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName($"{Module}.Outer.Inner"), out _));
            Assert.True(loaded.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName($"{Module}.OuterOther"), out _));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- Harness ---------------------------------------------------------------------------

    private static void RegisterClassRecord(ModuleTypeDatabase db, string moduleQualifiedName, string csharpName)
    {
        var swiftName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName);
        db.RegisterType(swiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(Module, csharpName),
            SwiftTypeName = swiftName,
            MetadataAccessor = $"$s{csharpName}CMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Class,
        });
    }


    /// <summary>
    /// Begins an ambient emission attempt whose poison list withdraws each supplied type at its
    /// whole-type surface — the same seed shape <c>WrapperDenylistSeed.Build</c> produces from an
    /// ingestion-quarantine closure.
    /// </summary>
    private static EmissionAttempt BeginAttempt(params TypeDecl[] withdrawnTypes)
    {
        var units = new HashSet<RecoveryUnitId>(
            withdrawnTypes.Select(t => RecoveryUnitId.Create(DeclIdFactory.ForType(t), RecoveryScope.TypeSurface)));
        return EmissionAttempt.Begin(
            WrapperDenylistSeed.Build(units, static _ => EmitterFaultOrigin.IngestionWithdrawal));
    }

    private static ModuleDecl MakeModule(params TypeDecl[] types)
    {
        var module = new ModuleDecl
        {
            Name = Module,
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
        };

        foreach (var type in types)
        {
            type.ParentDecl = module;
            type.ModuleDecl = module;
            module.Types.Add(type);
        }

        return module;
    }

    private static EnumDecl MakeErrorEnum(string name) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
        MangledName = $"$s{name}O",
        IsFrozen = true,
        MetadataAccessor = $"$s{name}OMa",
        GenericParameters = new List<GenericArgumentDecl>(),
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Cases = new List<EnumCaseDecl>
        {
            new()
            {
                Name = "failed",
                ParentDecl = null,
                ModuleDecl = null,
                MangledName = $"$s{name}failed",
                AssociatedValues = new List<TypeSpec>(),
            },
        },
        Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                string.Empty),
        },
        AvailabilityAnnotations = null,
    };

    /// <summary>
    /// A class shaped for the theme bridge: a static <c>shared</c> singleton of its own type plus a
    /// settable SwiftUI.Color instance property. Also serves the ownership-manifest test, which
    /// only needs a public class.
    /// </summary>
    private static ClassDecl MakeThemeClass(string name) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
        MangledName = $"$s{name}C",
        GenericParameters = new List<GenericArgumentDecl>(),
        Properties = new List<PropertyDecl>
        {
            MakeProperty("shared", $"{Module}.{name}", isStatic: true, hasSetter: false),
            MakeProperty("accentColor", "SwiftUI.Color", isStatic: false, hasSetter: true),
        },
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Conformances = new List<TypeConformance>(),
        SuperclassNames = new List<string>(),
        AvailabilityAnnotations = null,
    };

    private static PropertyDecl MakeProperty(string name, string typeName, bool isStatic, bool hasSetter)
    {
        var accessors = new List<AccessorDecl>
        {
            new GetAccessorDecl { Method = MakeAccessorMethod($"{name}_getter", isStatic) },
        };
        if (hasSetter)
            accessors.Add(new SetAccessorDecl { Method = MakeAccessorMethod($"{name}_setter", isStatic) });

        return new PropertyDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = new NamedTypeSpec(typeName),
            HasStorage = true,
            IsStatic = isStatic,
            Accessors = accessors,
        };
    }

    private static MethodDecl MakeAccessorMethod(string name, bool isStatic) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        MangledName = $"$s{name}",
        CSSignature = new List<ArgumentDecl>(),
        MethodType = isStatic ? MethodType.Static : MethodType.Instance,
        IsConstructor = false,
        Throws = false,
        IsAsync = false,
        GenericParameters = new List<GenericArgumentDecl>(),
        IsSynthesizedAccessor = true,
    };
}
