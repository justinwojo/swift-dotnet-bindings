#nullable enable
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ProtocolExtensionDefaultsIndex — the index used by ProtocolConformanceValidator
/// to recognize protocol extension default implementations.
/// </summary>
public class ProtocolExtensionDefaultsIndexTests
{
    [Fact]
    public void HasMethodDefault_DirectProtocol_ReturnsTrue()
    {
        // Extension on VectorAnimation.AnyInterpolatable provides _interpolate default (direct protocol lookup)
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["VectorAnimation.AnyInterpolatable"] = new()
            {
                CreateExtensionMethod("_interpolate", "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        Assert.True(index.HasMethodDefault("VectorAnimation.AnyInterpolatable",
            "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)"));
    }

    [Fact]
    public void HasMethodDefault_NoMatch_ReturnsFalse()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["VectorAnimation.AnyInterpolatable"] = new()
            {
                CreateExtensionMethod("_interpolate", "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        Assert.False(index.HasMethodDefault("VectorAnimation.AnyInterpolatable", "nonExistent()"));
    }

    [Fact]
    public void HasMethodDefault_ConstrainedExtension_IsIndexed()
    {
        // Constrained extensions (where T: SomeProtocol) ARE indexed — the Swift runtime
        // supplies the witness for any concrete type that satisfies the where-clause,
        // and the generator emits the corresponding C# requirement as a DIM that throws
        // NotSupportedException so the conformance compiles. Reproduces a constrained-
        // extension default on a protocol requirement (a DatabaseValueConvertible property
        // provided via the constrained RawRepresentable extension). The validator's
        // per-conformance-set overload narrows further when the conformance set actually applies.
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["VectorAnimation.AnyInterpolatable"] = new()
            {
                CreateExtensionMethod("_interpolate", "_interpolate(to:amount:)", whereConstraints: new() { "Self.Value : Comparable" })
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        Assert.True(index.HasMethodDefault("VectorAnimation.AnyInterpolatable", "_interpolate(to:amount:)"));
    }

    [Fact]
    public void HasPropertyDefault_ConstrainedExtension_DirectProto_AcceptedByNarrowOverload()
    {
        // The per-conformance-set narrow overload (HasPropertyDefault with concreteConformances)
        // returns true for a constrained default declared DIRECTLY on the queried protocol —
        // the conformance-set filter narrows sub-protocol providers, not direct-protocol entries.
        // The concrete conformer only directly conforms to DatabaseValueConvertible — not to any
        // sub-protocol that provides the default — so the narrow overload must accept the
        // direct-protocol constrained default by name match alone. The Swift typechecker has
        // already enforced the where-clause; the validator's job here is to confirm a witness
        // exists, not to re-derive Swift's where-clause logic.
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["RecordStore.DatabaseValueConvertible"] = new()
            {
                CreateExtensionProperty("databaseValue",
                    whereConstraints: new() { "Self : RawRepresentable", "Self.RawValue : DatabaseValueConvertible" })
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        // Conformance set contains ONLY the queried protocol — no sub-protocol detour.
        var conformances = new HashSet<string> { "RecordStore.DatabaseValueConvertible" };
        Assert.True(index.HasPropertyDefault("RecordStore.DatabaseValueConvertible", "databaseValue", conformances));
    }

    [Fact]
    public void HasMethodDefault_ConstrainedExtension_OnSubProtocol_RequiresConformance()
    {
        // The per-conformance-set narrow overload still requires sub-protocol membership for
        // INDIRECT defaults — broadening to include constrained extensions does not loosen
        // the sub-protocol gate. A constrained default that lives on a sub-protocol still
        // only counts when the concrete type actually conforms to that sub-protocol.
        var protocols = new List<ProtocolDecl>
        {
            CreateProtocolDecl("AnyInterpolatable", "VectorAnimation"),
            CreateProtocolDecl("Interpolatable", "VectorAnimation", inheritedFrom: "VectorAnimation.AnyInterpolatable")
        };
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["VectorAnimation.Interpolatable"] = new()
            {
                CreateExtensionMethod("_interpolate", "_interpolate(to:amount:)",
                    whereConstraints: new() { "Self.Value : Comparable" })
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, protocols);

        // Conforming to the sub-protocol → default is reachable.
        var withSub = new HashSet<string> { "VectorAnimation.AnyInterpolatable", "VectorAnimation.Interpolatable" };
        Assert.True(index.HasMethodDefault("VectorAnimation.AnyInterpolatable", "_interpolate(to:amount:)", withSub));

        // Conforming only to the parent → constrained sub-protocol default does NOT apply.
        var onlyParent = new HashSet<string> { "VectorAnimation.AnyInterpolatable" };
        Assert.False(index.HasMethodDefault("VectorAnimation.AnyInterpolatable", "_interpolate(to:amount:)", onlyParent));
    }

    [Fact]
    public void HasMethodDefault_SubProtocol_SatisfiesParent_ViaInheritanceGraph()
    {
        // Interpolatable inherits AnyInterpolatable. Extension on Interpolatable provides _interpolate.
        // With the inheritance graph enabled, sub-protocol defaults DO satisfy parent
        // protocol requirements (Interpolatable inherits from AnyInterpolatable).
        var protocols = new List<ProtocolDecl>
        {
            CreateProtocolDecl("AnyInterpolatable", "VectorAnimation"),
            CreateProtocolDecl("Interpolatable", "VectorAnimation", inheritedFrom: "VectorAnimation.AnyInterpolatable")
        };

        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["VectorAnimation.Interpolatable"] = new()
            {
                CreateExtensionMethod("_interpolate", "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, protocols);

        // Sub-protocol default is found for parent via inheritance graph
        Assert.True(index.HasMethodDefault("VectorAnimation.AnyInterpolatable",
            "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)"));
    }

    [Fact]
    public void HasMethodDefault_SubProtocol_WithConcreteConformances_InheritanceGraphEnabled()
    {
        // Interpolatable inherits AnyInterpolatable.
        // With the inheritance graph enabled, sub-protocol defaults are found
        // for parent protocol queries when the concrete type conforms to both.
        var protocols = new List<ProtocolDecl>
        {
            CreateProtocolDecl("AnyInterpolatable", "VectorAnimation"),
            CreateProtocolDecl("Interpolatable", "VectorAnimation", inheritedFrom: "VectorAnimation.AnyInterpolatable")
        };

        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["VectorAnimation.Interpolatable"] = new()
            {
                CreateExtensionMethod("_interpolate", "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, protocols);

        // Sub-protocol default IS found for parent via inheritance graph
        var conformances = new HashSet<string> { "VectorAnimation.AnyInterpolatable", "VectorAnimation.Interpolatable" };
        Assert.True(index.HasMethodDefault("VectorAnimation.AnyInterpolatable",
            "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)", conformances));

        // Without conformance to the sub-protocol, its default doesn't apply
        var onlyParent = new HashSet<string> { "VectorAnimation.AnyInterpolatable" };
        Assert.False(index.HasMethodDefault("VectorAnimation.AnyInterpolatable",
            "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)", onlyParent));
    }

    [Fact]
    public void HasMethodDefault_QualifiedNameMatching_NoCrossModuleCollision()
    {
        // Two different modules with same protocol name — should not cross-match
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["ModuleA.Parser"] = new()
            {
                CreateExtensionMethod("parse", "parse()")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        Assert.True(index.HasMethodDefault("ModuleA.Parser", "parse()"));
        Assert.False(index.HasMethodDefault("ModuleB.Parser", "parse()"));
    }

    [Fact]
    public void HasPropertyDefault_DirectProtocol_ReturnsTrue()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Configurable"] = new()
            {
                CreateExtensionProperty("defaultConfig")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        var conformances = new HashSet<string> { "TestModule.Configurable" };
        Assert.True(index.HasPropertyDefault("TestModule.Configurable", "defaultConfig", conformances));
        Assert.False(index.HasPropertyDefault("TestModule.Configurable", "nonExistent", conformances));
    }

    [Fact]
    public void HasDirectMethodDefault_DirectProtocol_ReturnsTrue()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Configurable"] = new()
            {
                CreateExtensionMethod("configure", "configure()")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        Assert.True(index.HasDirectMethodDefault("TestModule.Configurable", "configure()"));
        Assert.False(index.HasDirectMethodDefault("TestModule.Configurable", "nonExistent()"));
    }

    [Fact]
    public void HasDirectMethodDefault_SubProtocolDefault_ReturnsFalseForParent()
    {
        // Extension on Worker (sub-protocol) provides process() — parent AnyWorker should NOT match
        var protocols = new List<ProtocolDecl>
        {
            CreateProtocolDecl("AnyWorker", "TestModule"),
            CreateProtocolDecl("Worker", "TestModule", inheritedFrom: "TestModule.AnyWorker")
        };
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Worker"] = new()
            {
                CreateExtensionMethod("process", "process()")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, protocols);

        // Direct check on parent → false (default is on sub-protocol, not parent directly)
        Assert.False(index.HasDirectMethodDefault("TestModule.AnyWorker", "process()"));
        // Direct check on sub-protocol → true
        Assert.True(index.HasDirectMethodDefault("TestModule.Worker", "process()"));
        // Full traversal on parent → true (inheritance graph finds sub-protocol default)
        Assert.True(index.HasMethodDefault("TestModule.AnyWorker", "process()"));
    }

    [Fact]
    public void HasDirectPropertyDefault_DirectProtocol_ReturnsTrue()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Themed"] = new()
            {
                CreateExtensionProperty("color")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        Assert.True(index.HasDirectPropertyDefault("TestModule.Themed", "color"));
        Assert.False(index.HasDirectPropertyDefault("TestModule.Themed", "nonExistent"));
    }

    [Fact]
    public void HasPropertyDefault_GetterOnlyDefault_SatisfiesGetterRequirement()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Readable"] = new()
            {
                CreateExtensionProperty("value") // getter-only default
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        var conformances = new HashSet<string> { "TestModule.Readable" };
        // Getter-only requirement → satisfied by getter-only default
        Assert.True(index.HasPropertyDefault("TestModule.Readable", "value", conformances, requiresSetter: false));
    }

    [Fact]
    public void HasPropertyDefault_GetterOnlyDefault_DoesNotSatisfySetterRequirement()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.ReadWrite"] = new()
            {
                CreateExtensionProperty("value") // getter-only default
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        var conformances = new HashSet<string> { "TestModule.ReadWrite" };
        // { get set } requirement → NOT satisfied by getter-only default
        Assert.False(index.HasPropertyDefault("TestModule.ReadWrite", "value", conformances, requiresSetter: true));
    }

    [Fact]
    public void HasPropertyDefault_GetSetDefault_SatisfiesSetterRequirement()
    {
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.ReadWrite"] = new()
            {
                CreateExtensionProperty("value", hasSetter: true) // { get set } default
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());

        var conformances = new HashSet<string> { "TestModule.ReadWrite" };
        // { get set } requirement → satisfied by { get set } default
        Assert.True(index.HasPropertyDefault("TestModule.ReadWrite", "value", conformances, requiresSetter: true));
        // { get } requirement → also satisfied
        Assert.True(index.HasPropertyDefault("TestModule.ReadWrite", "value", conformances, requiresSetter: false));
    }

    #region Phantom Defaults Detection

    [Fact]
    public void DetectPhantomDefaults_PropertyMissingFromAllConformers_BecomesDefault()
    {
        // Protocol AnyValueProvider requires typeErasedStorage, but no conforming type has it.
        // Models the pattern where the PAT extension is invisible in the ABI JSON.
        var moduleDecl = CreateModuleDeclWithTypes("VectorAnimation");
        var protocol = CreateProtocolDeclWithProperties("AnyValueProvider", "VectorAnimation",
            new[] { ("typeErasedStorage", false), ("valueType", false) });
        protocol.Methods.Add(CreateVoidMethodForProtocol("hasUpdate", moduleDecl));
        moduleDecl.Protocols.Add(protocol);

        // FloatValueProvider conforms but only has valueType and hasUpdate — NOT typeErasedStorage
        var floatProvider = CreateClassDeclWithConformance("FloatValueProvider", moduleDecl, "AnyValueProvider");
        floatProvider.Properties.Add(CreateSimpleProperty("valueType", floatProvider, moduleDecl));
        floatProvider.Methods.Add(CreateSimpleMethod("hasUpdate", floatProvider, moduleDecl));
        moduleDecl.Types.Add(floatProvider);

        var index = new ProtocolExtensionDefaultsIndex(new(), moduleDecl.Protocols);
        index.DetectPhantomDefaults(moduleDecl);

        // typeErasedStorage should be detected as a phantom default
        Assert.True(index.HasDirectPropertyDefault("VectorAnimation.AnyValueProvider", "typeErasedStorage"));
        // valueType should NOT be a phantom default (present on FloatValueProvider)
        Assert.False(index.HasDirectPropertyDefault("VectorAnimation.AnyValueProvider", "valueType"));
    }

    [Fact]
    public void DetectPhantomDefaults_MethodMissingFromAllConformers_BecomesDefault()
    {
        var moduleDecl = CreateModuleDeclWithTypes("TestModule");
        var protocol = CreateProtocolDeclWithMethods("Worker", "TestModule",
            new[] { "process", "cleanup" });
        moduleDecl.Protocols.Add(protocol);

        // ConcreteWorker conforms but only has process — NOT cleanup
        var worker = CreateClassDeclWithConformance("ConcreteWorker", moduleDecl, "Worker");
        worker.Methods.Add(CreateSimpleMethod("process", worker, moduleDecl));
        moduleDecl.Types.Add(worker);

        var index = new ProtocolExtensionDefaultsIndex(new(), moduleDecl.Protocols);
        index.DetectPhantomDefaults(moduleDecl);

        // cleanup should be a phantom default, process should not
        Assert.True(index.HasDirectMethodDefault("TestModule.Worker", "cleanup()"));
        Assert.False(index.HasDirectMethodDefault("TestModule.Worker", "process()"));
    }

    [Fact]
    public void DetectPhantomDefaults_AllConformersHaveProperty_NotPhantomDefault()
    {
        var moduleDecl = CreateModuleDeclWithTypes("TestModule");
        var protocol = CreateProtocolDeclWithProperties("Describable", "TestModule",
            new[] { ("description", false) });
        moduleDecl.Protocols.Add(protocol);

        // Both conforming types have description
        var typeA = CreateClassDeclWithConformance("TypeA", moduleDecl, "Describable");
        typeA.Properties.Add(CreateSimpleProperty("description", typeA, moduleDecl));
        moduleDecl.Types.Add(typeA);

        var typeB = CreateClassDeclWithConformance("TypeB", moduleDecl, "Describable");
        typeB.Properties.Add(CreateSimpleProperty("description", typeB, moduleDecl));
        moduleDecl.Types.Add(typeB);

        var index = new ProtocolExtensionDefaultsIndex(new(), moduleDecl.Protocols);
        index.DetectPhantomDefaults(moduleDecl);

        Assert.False(index.HasDirectPropertyDefault("TestModule.Describable", "description"));
    }

    [Fact]
    public void DetectPhantomDefaults_NoConformingTypes_NoPhantomDefaults()
    {
        var moduleDecl = CreateModuleDeclWithTypes("TestModule");
        var protocol = CreateProtocolDeclWithProperties("Orphan", "TestModule",
            new[] { ("data", false) });
        moduleDecl.Protocols.Add(protocol);
        // No types conform to Orphan

        var index = new ProtocolExtensionDefaultsIndex(new(), moduleDecl.Protocols);
        index.DetectPhantomDefaults(moduleDecl);

        Assert.False(index.HasDirectPropertyDefault("TestModule.Orphan", "data"));
    }

    [Fact]
    public void DetectPhantomDefaults_ExistingExtensionDefault_NotDuplicated()
    {
        var moduleDecl = CreateModuleDeclWithTypes("TestModule");
        var protocol = CreateProtocolDeclWithProperties("Styled", "TestModule",
            new[] { ("theme", false) });
        moduleDecl.Protocols.Add(protocol);

        // ConcreteStyled conforms but doesn't have theme
        var styled = CreateClassDeclWithConformance("ConcreteStyled", moduleDecl, "Styled");
        moduleDecl.Types.Add(styled);

        // But theme already has a known extension default
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Styled"] = new() { CreateExtensionProperty("theme") }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, moduleDecl.Protocols);
        index.DetectPhantomDefaults(moduleDecl);

        // Should still report true (was already a default before phantom detection)
        Assert.True(index.HasDirectPropertyDefault("TestModule.Styled", "theme"));
    }

    [Fact]
    public void DetectPhantomDefaults_PropertyWithSetter_SetterDefaultAlsoDetected()
    {
        var moduleDecl = CreateModuleDeclWithTypes("TestModule");
        var protocol = CreateProtocolDeclWithProperties("Mutable", "TestModule",
            new[] { ("value", true) }); // { get set }
        moduleDecl.Protocols.Add(protocol);

        var concrete = CreateClassDeclWithConformance("ConcreteMutable", moduleDecl, "Mutable");
        moduleDecl.Types.Add(concrete);

        var index = new ProtocolExtensionDefaultsIndex(new(), moduleDecl.Protocols);
        index.DetectPhantomDefaults(moduleDecl);

        Assert.True(index.HasDirectPropertyDefault("TestModule.Mutable", "value"));
        Assert.True(index.HasDirectPropertyDefault("TestModule.Mutable", "value", requiresSetter: true));
    }

    [Fact]
    public void DetectPhantomDefaults_StaticPropertyIgnored()
    {
        var moduleDecl = CreateModuleDeclWithTypes("TestModule");
        var protocol = CreateProtocolDeclWithProperties("HasStatic", "TestModule", Array.Empty<(string, bool)>());
        // Add a static property manually
        var staticProp = CreateSimpleProperty("shared", null, moduleDecl);
        staticProp.IsStatic = true;
        protocol.Properties.Add(staticProp);
        moduleDecl.Protocols.Add(protocol);

        var concrete = CreateClassDeclWithConformance("ConcreteHasStatic", moduleDecl, "HasStatic");
        moduleDecl.Types.Add(concrete);

        var index = new ProtocolExtensionDefaultsIndex(new(), moduleDecl.Protocols);
        index.DetectPhantomDefaults(moduleDecl);

        // Static properties are not checked for phantom defaults
        Assert.False(index.HasDirectPropertyDefault("TestModule.HasStatic", "shared"));
    }

    #endregion

    #region Helpers

    private static ProtocolExtensionMethodDecl CreateExtensionMethod(string methodName, string printedName,
        List<string>? whereConstraints = null)
    {
        return new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = "",
            MethodName = methodName,
            PrintedName = printedName,
            RawSignature = $"func {methodName}()",
            ReturnsSelf = false,
            IsMainActorIsolated = false,
            IsStatic = false,
            IsProperty = false,
            HasSetter = false,
            IsDeprecated = false,
            IsMutating = false,
            WhereConstraints = whereConstraints ?? new List<string>()
        };
    }

    private static ProtocolExtensionMethodDecl CreateExtensionProperty(string propertyName, bool hasSetter = false,
        List<string>? whereConstraints = null)
    {
        return new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = "",
            MethodName = propertyName,
            PrintedName = propertyName,
            RawSignature = hasSetter ? $"var {propertyName}: Int {{ get set }}" : $"var {propertyName}: Int {{ get }}",
            ReturnsSelf = false,
            IsMainActorIsolated = false,
            IsStatic = false,
            IsProperty = true,
            HasSetter = hasSetter,
            IsDeprecated = false,
            IsMutating = false,
            WhereConstraints = whereConstraints ?? new List<string>()
        };
    }

    private static ProtocolDecl CreateProtocolDecl(string name, string moduleName,
        string? inheritedFrom = null, string? inheritedFrom2 = null)
    {
        var inherited = new List<NamedTypeSpec>();
        if (inheritedFrom != null)
            inherited.Add(new NamedTypeSpec(inheritedFrom));
        if (inheritedFrom2 != null)
            inherited.Add(new NamedTypeSpec(inheritedFrom2));

        var moduleDecl = new ModuleDecl
        {
            Name = moduleName,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName}{name}P",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = inherited,
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static ModuleDecl CreateModuleDeclWithTypes(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ProtocolDecl CreateProtocolDeclWithProperties(string name, string moduleName,
        (string name, bool hasSetter)[] properties)
    {
        var proto = CreateProtocolDecl(name, moduleName);
        foreach (var (propName, hasSetter) in properties)
        {
            var accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = null! } };
            if (hasSetter) accessors.Add(new SetAccessorDecl { Method = null! });
            proto.Properties.Add(new PropertyDecl
            {
                Name = propName,
                SwiftTypeSpec = new NamedTypeSpec($"{moduleName}.SomeType"),
                IsStatic = false,
                HasStorage = false,
                Accessors = accessors,
                ParentDecl = proto,
                ModuleDecl = null
            });
        }
        return proto;
    }

    private static ProtocolDecl CreateProtocolDeclWithMethods(string name, string moduleName, string[] methodNames)
    {
        var proto = CreateProtocolDecl(name, moduleName);
        foreach (var methodName in methodNames)
        {
            proto.Methods.Add(new MethodDecl
            {
                Name = methodName,
                MangledName = $"$s{moduleName}{methodName}F",
                MethodType = MethodType.Instance,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>
                {
                    new() { SwiftTypeSpec = TupleTypeSpec.Empty, Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
                },
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = proto,
                ModuleDecl = null,
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Public
            });
        }
        return proto;
    }

    private static ClassDecl CreateClassDeclWithConformance(string name, ModuleDecl moduleDecl, string protocolName)
    {
        var cls = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name}{name}C",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>
            {
                new(
                    SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
                    SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{protocolName}"),
                    $"$s{moduleDecl.Name}{name}_{protocolName}Conformance")
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        return cls;
    }

    private static PropertyDecl CreateSimpleProperty(string name, BaseDecl? parent, ModuleDecl moduleDecl)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = null! } },
            ParentDecl = parent,
            ModuleDecl = moduleDecl
        };
    }

    private static MethodDecl CreateSimpleMethod(string name, BaseDecl? parent, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}F",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = TupleTypeSpec.Empty, Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateVoidMethodForProtocol(string name, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}F",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = TupleTypeSpec.Empty, Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    #endregion

    #region Inheritance Graph Tests

    [Fact]
    public void InheritanceGraph_ThreeLevelChain_TransitiveDefault()
    {
        // C inherits B inherits A. Extension default on C satisfies A's requirement.
        var protocols = new List<ProtocolDecl>
        {
            CreateProtocolDecl("A", "TestModule"),
            CreateProtocolDecl("B", "TestModule", inheritedFrom: "TestModule.A"),
            CreateProtocolDecl("C", "TestModule", inheritedFrom: "TestModule.B")
        };

        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.C"] = new()
            {
                CreateExtensionMethod("doWork", "doWork()")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, protocols);

        // C's default should satisfy A's requirement via B (transitive)
        Assert.True(index.HasMethodDefault("TestModule.A", "doWork()"));
        Assert.True(index.HasMethodDefault("TestModule.B", "doWork()"));
        Assert.True(index.HasMethodDefault("TestModule.C", "doWork()"));
    }

    [Fact]
    public void InheritanceGraph_DiamondInheritance_DefaultResolvesCorrectly()
    {
        // D inherits both B and C, which both inherit A (diamond)
        var protocols = new List<ProtocolDecl>
        {
            CreateProtocolDecl("A", "TestModule"),
            CreateProtocolDecl("B", "TestModule", inheritedFrom: "TestModule.A"),
            CreateProtocolDecl("C", "TestModule", inheritedFrom: "TestModule.A"),
            CreateProtocolDecl("D", "TestModule", inheritedFrom: "TestModule.B", inheritedFrom2: "TestModule.C")
        };

        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.D"] = new()
            {
                CreateExtensionMethod("handle", "handle()")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, protocols);

        // D's default should satisfy A's requirement via both paths
        Assert.True(index.HasMethodDefault("TestModule.A", "handle()"));
        Assert.True(index.HasMethodDefault("TestModule.B", "handle()"));
        Assert.True(index.HasMethodDefault("TestModule.C", "handle()"));
    }

    [Fact]
    public void InheritanceGraph_NoInheritance_SubProtocolDefaultDoesNotSatisfyUnrelatedProtocol()
    {
        // X and Y are unrelated protocols. Y's default should not satisfy X's requirement.
        var protocols = new List<ProtocolDecl>
        {
            CreateProtocolDecl("X", "TestModule"),
            CreateProtocolDecl("Y", "TestModule")
        };

        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Y"] = new()
            {
                CreateExtensionMethod("doWork", "doWork()")
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, protocols);

        Assert.False(index.HasMethodDefault("TestModule.X", "doWork()"));
        Assert.True(index.HasMethodDefault("TestModule.Y", "doWork()"));
    }

    #endregion

}
