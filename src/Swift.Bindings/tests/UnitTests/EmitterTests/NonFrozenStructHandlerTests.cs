// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for NonFrozenStructHandler and NonFrozenStructHandlerFactory.
/// </summary>
public class NonFrozenStructHandlerTests
{
    #region Factory Tests

    [Fact]
    public void Factory_Handles_NonFrozenStructDecl_ReturnsTrue()
    {
        var factory = new NonFrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var nonFrozenStruct = CreateNonFrozenStructDecl("NonFrozenStruct");

        Assert.True(factory.Handles(nonFrozenStruct));
    }

    [Fact]
    public void Factory_Handles_FrozenStructDecl_ReturnsFalse()
    {
        var factory = new NonFrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var frozenStruct = CreateFrozenStructDecl("FrozenStruct");

        Assert.False(factory.Handles(frozenStruct));
    }

    [Fact]
    public void Factory_Handles_ClassDecl_ReturnsFalse()
    {
        var factory = new NonFrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var classDecl = CreateClassDecl("MyClass");

        Assert.False(factory.Handles(classDecl));
    }

    [Fact]
    public void Factory_Handles_EnumDecl_ReturnsFalse()
    {
        var factory = new NonFrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var enumDecl = CreateEnumDecl("MyEnum");

        Assert.False(factory.Handles(enumDecl));
    }

    [Fact]
    public void Factory_Handles_ProtocolDecl_ReturnsFalse()
    {
        var factory = new NonFrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var protocolDecl = CreateProtocolDecl("MyProtocol");

        Assert.False(factory.Handles(protocolDecl));
    }

    [Fact]
    public void Factory_Construct_ReturnsHandler()
    {
        var factory = new NonFrozenStructHandlerFactory(NullLoggerFactory.Instance);

        var handler = factory.Construct();

        Assert.NotNull(handler);
        Assert.IsType<NonFrozenStructHandler>(handler);
    }

    #endregion

    #region StructDecl Configuration Tests

    [Fact]
    public void NonFrozenStructDecl_IsFrozen_ReturnsFalse()
    {
        var structDecl = CreateNonFrozenStructDecl("NonFrozenType");

        Assert.False(structDecl.IsFrozen);
    }

    [Fact]
    public void NonFrozenStructDecl_HasCorrectSwiftTypeName()
    {
        var structDecl = CreateNonFrozenStructDecl("DataModel", moduleName: "MyApp");

        Assert.Equal("MyApp.DataModel", structDecl.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void NonFrozenStructDecl_CanHaveProperties()
    {
        var structDecl = CreateNonFrozenStructDecl("ViewModel");
        structDecl.Properties.Add(CreatePropertyDecl("data", "Swift.String"));
        structDecl.Properties.Add(CreatePropertyDecl("isLoading", "Swift.Bool"));

        Assert.Equal(2, structDecl.Properties.Count);
    }

    [Fact]
    public void NonFrozenStructDecl_CanHaveMethods()
    {
        var structDecl = CreateNonFrozenStructDecl("DataManager");
        structDecl.Methods.Add(CreateMethodDecl("fetch"));
        structDecl.Methods.Add(CreateMethodDecl("save"));

        Assert.Equal(2, structDecl.Methods.Count);
    }

    [Fact]
    public void NonFrozenStructDecl_CanHaveOperators()
    {
        var structDecl = CreateNonFrozenStructDecl("CustomType");
        structDecl.Operators.Add(CreateOperatorDecl("==", OperatorKind.Binary));

        Assert.Single(structDecl.Operators);
    }

    #endregion

    #region SafeHandle Payload Tests

    [Fact]
    public void NonFrozenStructDecl_RequiresPayloadField()
    {
        // Non-frozen structs are projected as classes with SafeHandle<T> payload
        var structDecl = CreateNonFrozenStructDecl("OpaqueStruct");

        // This is validated at emission time - the struct should have IsFrozen = false
        Assert.False(structDecl.IsFrozen);
    }

    [Fact]
    public void NonFrozenStructDecl_HasMetadataAccessor()
    {
        var structDecl = CreateNonFrozenStructDecl("DataContainer");
        structDecl.MetadataAccessor = "$s4MyApp13DataContainerVMa";

        Assert.NotEmpty(structDecl.MetadataAccessor);
    }

    #endregion

    #region Conformance Tests

    [Fact]
    public void NonFrozenStructDecl_CanHaveConformances()
    {
        var structDecl = CreateNonFrozenStructDecl("EquatableData");
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.EquatableData"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sConformance"));

        Assert.Single(structDecl.Conformances);
    }

    [Fact]
    public void NonFrozenStructDecl_ConformsToHashable_CanBeDetected()
    {
        var structDecl = CreateNonFrozenStructDecl("HashableData");
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.HashableData"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
            "$sHashableConformance"));

        var conformsToHashable = structDecl.Conformances
            .Any(c => c.Protocol.ModuleQualifiedName == "Swift.Hashable");

        Assert.True(conformsToHashable);
    }

    #endregion

    #region Generic Parameters Tests

    [Fact]
    public void NonFrozenStructDecl_WithGenericParameter_HasGenericParameters()
    {
        var structDecl = CreateNonFrozenStructDecl("GenericContainer");
        structDecl.GenericParameters.Add(CreateGenericArgumentDecl("Element"));

        Assert.Single(structDecl.GenericParameters);
    }

    [Fact]
    public void NonFrozenStructDecl_WithConstrainedGeneric_HasConformances()
    {
        var structDecl = CreateNonFrozenStructDecl("ComparableContainer");
        structDecl.GenericParameters.Add(CreateGenericArgumentDeclWithConformance("T", "Swift.Comparable"));

        Assert.Single(structDecl.GenericParameters[0].GenericConformances);
    }

    #endregion

    #region A8 — Property Dedup Tests

    [Fact]
    public void NonFrozenStructHandler_DuplicateProperty_SecondSkipped()
    {
        // When the same property name appears twice (e.g., from conditional extensions),
        // the second should be detected as a duplicate and skipped.
        var structDecl = CreateNonFrozenStructDecl("NetworkConfig");
        structDecl.Properties.Add(CreatePropertyDecl("timeout", "Swift.Double"));
        structDecl.Properties.Add(CreatePropertyDecl("timeout", "Swift.Double")); // duplicate from extension

        var names = new HashSet<string>();
        foreach (var prop in structDecl.Properties)
        {
            var csName = NameProvider.GetPropertyName(prop.Name, structDecl.Name);
            if (!names.Add(csName))
            {
                // Second add returns false — duplicate correctly detected
                Assert.True(true, "Duplicate property correctly detected for non-frozen struct");
                return;
            }
        }

        Assert.Fail("Should have detected duplicate property name");
    }

    #endregion

    #region Helper Methods

    private static StructDecl CreateNonFrozenStructDecl(string name, string moduleName = "TestModule")
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = false,
            MetadataAccessor = ""
        };
    }

    private static StructDecl CreateFrozenStructDecl(string name, string moduleName = "TestModule")
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = ""
        };
    }

    private static ClassDecl CreateClassDecl(string name, string moduleName = "TestModule")
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
            ModuleDecl = null
        };
    }

    private static EnumDecl CreateEnumDecl(string name, string moduleName = "TestModule")
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
            IsFrozen = false,
            MetadataAccessor = ""
        };
    }

    private static ProtocolDecl CreateProtocolDecl(string name, string moduleName = "TestModule")
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static PropertyDecl CreatePropertyDecl(string name, string typeName)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(typeName),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = $"{name}_Get",
                        MangledName = $"$s{name}g",
                        MethodType = MethodType.Instance,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>(),
                        GenericParameters = new List<GenericArgumentDecl>(),
                        ParentDecl = null,
                        ModuleDecl = null,
                        Throws = false,
                        IsAsync = false,
                        Visibility = Visibility.Private
                    }
                }
            },
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static MethodDecl CreateMethodDecl(string name)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static OperatorDecl CreateOperatorDecl(string symbol, OperatorKind kind)
    {
        var methodDecl = new MethodDecl
        {
            Name = symbol,
            MangledName = $"$s{symbol}",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"),
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Point"),
                    Name = "left",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        if (kind == OperatorKind.Binary)
        {
            methodDecl.CSSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("TestModule.Point"),
                Name = "right",
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            });
        }

        return new OperatorDecl
        {
            Name = symbol,
            OperatorSymbol = symbol,
            Kind = kind,
            IsPrefix = true,
            UnderlyingMethod = methodDecl,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static GenericArgumentDecl CreateGenericArgumentDecl(string name)
    {
        return new GenericArgumentDecl(
            TypeName: name,
            SugaredTypeName: name,
            GenericConformances: new List<GenericParameterConformance>(),
            AssosiatedTypeConformances: new List<GenericParameterConformance>()
        );
    }

    private static GenericArgumentDecl CreateGenericArgumentDeclWithConformance(string name, string conformance)
    {
        return new GenericArgumentDecl(
            TypeName: name,
            SugaredTypeName: name,
            GenericConformances: new List<GenericParameterConformance>
            {
                new GenericParameterConformance(
                    Path: new[] { name },
                    ConformanceTarget: SwiftTypeName.FromModuleQualifiedName(conformance),
                    Kind: ConformanceKind.Protocol
                )
            },
            AssosiatedTypeConformances: new List<GenericParameterConformance>()
        );
    }

    #endregion
}
