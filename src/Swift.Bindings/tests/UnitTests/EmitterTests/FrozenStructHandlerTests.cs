// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for FrozenStructHandler and FrozenStructHandlerFactory.
/// </summary>
public class FrozenStructHandlerTests
{
    #region Factory Tests

    [Fact]
    public void Factory_Handles_FrozenStructDecl_ReturnsTrue()
    {
        var factory = new FrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var frozenStruct = CreateFrozenStructDecl("Point");

        Assert.True(factory.Handles(frozenStruct));
    }

    [Fact]
    public void Factory_Handles_NonFrozenStructDecl_ReturnsFalse()
    {
        var factory = new FrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var nonFrozenStruct = CreateNonFrozenStructDecl("NonFrozenStruct");

        Assert.False(factory.Handles(nonFrozenStruct));
    }

    [Fact]
    public void Factory_Handles_ClassDecl_ReturnsFalse()
    {
        var factory = new FrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var classDecl = CreateClassDecl("MyClass");

        Assert.False(factory.Handles(classDecl));
    }

    [Fact]
    public void Factory_Handles_EnumDecl_ReturnsFalse()
    {
        var factory = new FrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var enumDecl = CreateEnumDecl("MyEnum");

        Assert.False(factory.Handles(enumDecl));
    }

    [Fact]
    public void Factory_Handles_ProtocolDecl_ReturnsFalse()
    {
        var factory = new FrozenStructHandlerFactory(NullLoggerFactory.Instance);
        var protocolDecl = CreateProtocolDecl("MyProtocol");

        Assert.False(factory.Handles(protocolDecl));
    }

    [Fact]
    public void Factory_Construct_ReturnsHandler()
    {
        var factory = new FrozenStructHandlerFactory(NullLoggerFactory.Instance);

        var handler = factory.Construct();

        Assert.NotNull(handler);
        Assert.IsType<FrozenStructHandler>(handler);
    }

    #endregion

    #region StructDecl Configuration Tests

    [Fact]
    public void FrozenStructDecl_IsFrozen_ReturnsTrue()
    {
        var structDecl = CreateFrozenStructDecl("CGPoint");

        Assert.True(structDecl.IsFrozen);
    }

    [Fact]
    public void FrozenStructDecl_HasCorrectSwiftTypeName()
    {
        var structDecl = CreateFrozenStructDecl("CGPoint", moduleName: "CoreGraphics");

        Assert.Equal("CoreGraphics.CGPoint", structDecl.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void FrozenStructDecl_CanHaveProperties()
    {
        var structDecl = CreateFrozenStructDecl("CGPoint");
        structDecl.Properties.Add(CreatePropertyDecl("x", "Swift.Double"));
        structDecl.Properties.Add(CreatePropertyDecl("y", "Swift.Double"));

        Assert.Equal(2, structDecl.Properties.Count);
    }

    [Fact]
    public void FrozenStructDecl_CanHaveMethods()
    {
        var structDecl = CreateFrozenStructDecl("CGPoint");
        structDecl.Methods.Add(CreateMethodDecl("distance"));

        Assert.Single(structDecl.Methods);
    }

    [Fact]
    public void FrozenStructDecl_CanHaveOperators()
    {
        var structDecl = CreateFrozenStructDecl("Vector");
        structDecl.Operators.Add(CreateOperatorDecl("+", OperatorKind.Binary));
        structDecl.Operators.Add(CreateOperatorDecl("-", OperatorKind.Binary));

        Assert.Equal(2, structDecl.Operators.Count);
    }

    [Fact]
    public void FrozenStructDecl_CanHaveNestedTypes()
    {
        var structDecl = CreateFrozenStructDecl("Container");
        structDecl.Types.Add(CreateFrozenStructDecl("InnerStruct", moduleName: "TestModule.Container"));

        Assert.Single(structDecl.Types);
    }

    [Fact]
    public void FrozenStructDecl_CanHaveConformances()
    {
        var structDecl = CreateFrozenStructDecl("EquatablePoint");
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.EquatablePoint"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sConformance"));

        Assert.Single(structDecl.Conformances);
    }

    [Fact]
    public void FrozenStructDecl_ConformsToEquatable_CanBeDetected()
    {
        var structDecl = CreateFrozenStructDecl("Point");
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sEquatableConformance"));

        var conformsToEquatable = structDecl.Conformances
            .Any(c => c.Protocol.ModuleQualifiedName == "Swift.Equatable");

        Assert.True(conformsToEquatable);
    }

    #endregion

    #region Generic Parameters Tests

    [Fact]
    public void FrozenStructDecl_WithGenericParameter_HasGenericParameters()
    {
        var structDecl = CreateFrozenStructDecl("Container");
        structDecl.GenericParameters.Add(CreateGenericArgumentDecl("T"));

        Assert.Single(structDecl.GenericParameters);
        Assert.Equal("T", structDecl.GenericParameters[0].TypeName);
    }

    [Fact]
    public void FrozenStructDecl_WithMultipleGenericParameters_CollectsAll()
    {
        var structDecl = CreateFrozenStructDecl("Pair");
        structDecl.GenericParameters.Add(CreateGenericArgumentDecl("T"));
        structDecl.GenericParameters.Add(CreateGenericArgumentDecl("U"));

        Assert.Equal(2, structDecl.GenericParameters.Count);
    }

    [Fact]
    public void FrozenStructDecl_WithConstrainedGeneric_HasConformances()
    {
        var structDecl = CreateFrozenStructDecl("EquatableContainer");
        structDecl.GenericParameters.Add(CreateGenericArgumentDeclWithConformance("T", "Swift.Equatable"));

        Assert.Single(structDecl.GenericParameters[0].GenericConformances);
    }

    #endregion

    #region Operator Support Tests

    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("*")]
    [InlineData("/")]
    [InlineData("==")]
    [InlineData("!=")]
    [InlineData("<")]
    [InlineData(">")]
    public void FrozenStructDecl_CanHaveArithmeticAndComparisonOperators(string symbol)
    {
        var structDecl = CreateFrozenStructDecl("Number");
        structDecl.Operators.Add(CreateOperatorDecl(symbol, OperatorKind.Binary));

        Assert.Single(structDecl.Operators);
        Assert.Equal(symbol, structDecl.Operators[0].OperatorSymbol);
    }

    [Theory]
    [InlineData("!")]
    [InlineData("~")]
    public void FrozenStructDecl_CanHaveUnaryOperators(string symbol)
    {
        var structDecl = CreateFrozenStructDecl("BitField");
        structDecl.Operators.Add(CreateOperatorDecl(symbol, OperatorKind.Unary));

        Assert.Single(structDecl.Operators);
        Assert.Equal(OperatorKind.Unary, structDecl.Operators[0].Kind);
    }

    [Fact]
    public void FrozenStructDecl_HasEqualityOperator_CanBeDetected()
    {
        var structDecl = CreateFrozenStructDecl("Point");
        structDecl.Operators.Add(CreateOperatorDecl("==", OperatorKind.Binary));

        var hasEquality = structDecl.Operators.Any(o => o.OperatorSymbol == "==");

        Assert.True(hasEquality);
    }

    #endregion

    #region Property Storage Tests

    [Fact]
    public void FrozenStructDecl_StoredProperty_HasStorageTrue()
    {
        var structDecl = CreateFrozenStructDecl("Point");
        var property = CreatePropertyDecl("x", "Swift.Double", hasStorage: true);
        structDecl.Properties.Add(property);

        Assert.True(structDecl.Properties[0].HasStorage);
    }

    [Fact]
    public void FrozenStructDecl_ComputedProperty_HasStorageFalse()
    {
        var structDecl = CreateFrozenStructDecl("Rectangle");
        var property = CreatePropertyDecl("area", "Swift.Double", hasStorage: false);
        structDecl.Properties.Add(property);

        Assert.False(structDecl.Properties[0].HasStorage);
    }

    [Fact]
    public void FrozenStructDecl_MixedStorageProperties_BothDetected()
    {
        var structDecl = CreateFrozenStructDecl("Rectangle");
        structDecl.Properties.Add(CreatePropertyDecl("width", "Swift.Double", hasStorage: true));
        structDecl.Properties.Add(CreatePropertyDecl("height", "Swift.Double", hasStorage: true));
        structDecl.Properties.Add(CreatePropertyDecl("area", "Swift.Double", hasStorage: false));

        var storedCount = structDecl.Properties.Count(p => p.HasStorage);
        var computedCount = structDecl.Properties.Count(p => !p.HasStorage);

        Assert.Equal(2, storedCount);
        Assert.Equal(1, computedCount);
    }

    #endregion

    #region Metadata Accessor Tests

    [Fact]
    public void FrozenStructDecl_HasMetadataAccessor()
    {
        var structDecl = CreateFrozenStructDecl("Point");
        structDecl.MetadataAccessor = "$s12CoreGraphics7CGPointVMa";

        Assert.NotEmpty(structDecl.MetadataAccessor);
    }

    [Fact]
    public void FrozenStructDecl_MetadataAccessorFormat_ContainsMaSuffix()
    {
        var structDecl = CreateFrozenStructDecl("Point");
        structDecl.MetadataAccessor = "$s12CoreGraphics7CGPointVMa";

        Assert.EndsWith("Ma", structDecl.MetadataAccessor);
    }

    #endregion

    #region Helper Methods

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

    private static PropertyDecl CreatePropertyDecl(string name, string typeName, bool hasStorage = false)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(typeName),
            IsStatic = false,
            HasStorage = hasStorage,
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

    private static OperatorDecl CreateOperatorDecl(string symbol, OperatorKind kind, bool isPrefix = true)
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
            IsPrefix = isPrefix,
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
