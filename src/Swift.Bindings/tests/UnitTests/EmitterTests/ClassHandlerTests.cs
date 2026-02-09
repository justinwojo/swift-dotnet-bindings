// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ClassHandler and ClassHandlerFactory.
/// </summary>
public class ClassHandlerTests
{
    #region Factory Tests

    [Fact]
    public void Factory_Handles_ClassDecl_ReturnsTrue()
    {
        var factory = new ClassHandlerFactory(NullLoggerFactory.Instance);
        var classDecl = CreateClassDecl("MyClass");

        Assert.True(factory.Handles(classDecl));
    }

    [Fact]
    public void Factory_Handles_StructDecl_ReturnsFalse()
    {
        var factory = new ClassHandlerFactory(NullLoggerFactory.Instance);
        var structDecl = CreateStructDecl("MyStruct");

        Assert.False(factory.Handles(structDecl));
    }

    [Fact]
    public void Factory_Handles_EnumDecl_ReturnsFalse()
    {
        var factory = new ClassHandlerFactory(NullLoggerFactory.Instance);
        var enumDecl = CreateEnumDecl("MyEnum");

        Assert.False(factory.Handles(enumDecl));
    }

    [Fact]
    public void Factory_Handles_ProtocolDecl_ReturnsFalse()
    {
        var factory = new ClassHandlerFactory(NullLoggerFactory.Instance);
        var protocolDecl = CreateProtocolDecl("MyProtocol");

        Assert.False(factory.Handles(protocolDecl));
    }

    [Fact]
    public void Factory_Construct_ReturnsHandler()
    {
        var factory = new ClassHandlerFactory(NullLoggerFactory.Instance);

        var handler = factory.Construct();

        Assert.NotNull(handler);
        Assert.IsType<ClassHandler>(handler);
    }

    #endregion

    #region ClassDecl Configuration Tests

    [Fact]
    public void ClassDecl_HasCorrectName()
    {
        var classDecl = CreateClassDecl("ImageLoader");

        Assert.Equal("ImageLoader", classDecl.Name);
    }

    [Fact]
    public void ClassDecl_HasCorrectSwiftTypeName()
    {
        var classDecl = CreateClassDecl("ImageLoader", moduleName: "Nuke");

        Assert.Equal("Nuke.ImageLoader", classDecl.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void ClassDecl_HasCorrectMangledName()
    {
        var classDecl = CreateClassDecl("MyClass");

        Assert.Contains("$s", classDecl.MangledName);
        Assert.Contains("CN", classDecl.MangledName);
    }

    [Fact]
    public void ClassDecl_InitializesEmptyCollections()
    {
        var classDecl = CreateClassDecl("EmptyClass");

        Assert.Empty(classDecl.Properties);
        Assert.Empty(classDecl.Methods);
        Assert.Empty(classDecl.Types);
        Assert.Empty(classDecl.Operators);
    }

    #endregion

    #region Properties Tests

    [Fact]
    public void ClassDecl_CanHaveProperties()
    {
        var classDecl = CreateClassDecl("DataModel");
        classDecl.Properties.Add(CreatePropertyDecl("name", "Swift.String"));
        classDecl.Properties.Add(CreatePropertyDecl("id", "Swift.Int"));

        Assert.Equal(2, classDecl.Properties.Count);
    }

    [Fact]
    public void ClassDecl_StaticProperty_IsStaticTrue()
    {
        var classDecl = CreateClassDecl("Singleton");
        classDecl.Properties.Add(CreatePropertyDecl("shared", "TestModule.Singleton", isStatic: true));

        Assert.True(classDecl.Properties[0].IsStatic);
    }

    [Fact]
    public void ClassDecl_InstanceProperty_IsStaticFalse()
    {
        var classDecl = CreateClassDecl("Person");
        classDecl.Properties.Add(CreatePropertyDecl("name", "Swift.String", isStatic: false));

        Assert.False(classDecl.Properties[0].IsStatic);
    }

    #endregion

    #region Methods Tests

    [Fact]
    public void ClassDecl_CanHaveMethods()
    {
        var classDecl = CreateClassDecl("NetworkManager");
        classDecl.Methods.Add(CreateMethodDecl("fetch"));
        classDecl.Methods.Add(CreateMethodDecl("post"));

        Assert.Equal(2, classDecl.Methods.Count);
    }

    [Fact]
    public void ClassDecl_CanHaveConstructor()
    {
        var classDecl = CreateClassDecl("Person");
        classDecl.Methods.Add(CreateMethodDecl("init", isConstructor: true));

        Assert.True(classDecl.Methods[0].IsConstructor);
    }

    [Fact]
    public void ClassDecl_CanHaveStaticMethod()
    {
        var classDecl = CreateClassDecl("Factory");
        classDecl.Methods.Add(CreateMethodDecl("create", isStatic: true));

        Assert.Equal(MethodType.Static, classDecl.Methods[0].MethodType);
    }

    [Fact]
    public void ClassDecl_CanHaveAsyncMethod()
    {
        var classDecl = CreateClassDecl("DataLoader");
        classDecl.Methods.Add(CreateMethodDecl("loadData", isAsync: true));

        Assert.True(classDecl.Methods[0].IsAsync);
    }

    [Fact]
    public void ClassDecl_CanHaveThrowingMethod()
    {
        var classDecl = CreateClassDecl("FileManager");
        classDecl.Methods.Add(CreateMethodDecl("readFile", throws: true));

        Assert.True(classDecl.Methods[0].Throws);
    }

    #endregion

    #region Conformance Tests

    [Fact]
    public void ClassDecl_CanHaveConformances()
    {
        var classDecl = CreateClassDecl("EquatableClass");
        classDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.EquatableClass"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sEquatableConformance"));

        Assert.Single(classDecl.Conformances);
    }

    [Fact]
    public void ClassDecl_ConformsToEquatable_CanBeDetected()
    {
        var classDecl = CreateClassDecl("Person");
        classDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Person"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sEquatableConformance"));

        var conformsToEquatable = classDecl.Conformances
            .Any(c => c.Protocol.ModuleQualifiedName == "Swift.Equatable");

        Assert.True(conformsToEquatable);
    }

    [Fact]
    public void ClassDecl_ConformsToMultipleProtocols()
    {
        var classDecl = CreateClassDecl("Model");
        classDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Model"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sEquatable"));
        classDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Model"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
            "$sHashable"));
        classDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Model"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Codable"),
            "$sCodable"));

        Assert.Equal(3, classDecl.Conformances.Count);
    }

    #endregion

    #region Generic Parameters Tests

    [Fact]
    public void ClassDecl_WithGenericParameter_HasGenericParameters()
    {
        var classDecl = CreateClassDecl("Box");
        classDecl.GenericParameters.Add(CreateGenericArgumentDecl("T"));

        Assert.Single(classDecl.GenericParameters);
        Assert.Equal("T", classDecl.GenericParameters[0].TypeName);
    }

    [Fact]
    public void ClassDecl_WithMultipleGenericParameters_CollectsAll()
    {
        var classDecl = CreateClassDecl("Dictionary");
        classDecl.GenericParameters.Add(CreateGenericArgumentDecl("Key"));
        classDecl.GenericParameters.Add(CreateGenericArgumentDecl("Value"));

        Assert.Equal(2, classDecl.GenericParameters.Count);
    }

    [Fact]
    public void ClassDecl_WithConstrainedGeneric_HasConformances()
    {
        var classDecl = CreateClassDecl("SortedContainer");
        classDecl.GenericParameters.Add(CreateGenericArgumentDeclWithConformance("T", "Swift.Comparable"));

        Assert.Single(classDecl.GenericParameters[0].GenericConformances);
    }

    #endregion

    #region Nested Types Tests

    [Fact]
    public void ClassDecl_CanHaveNestedTypes()
    {
        var classDecl = CreateClassDecl("OuterClass");
        classDecl.Types.Add(CreateClassDecl("InnerClass", moduleName: "TestModule.OuterClass"));

        Assert.Single(classDecl.Types);
    }

    [Fact]
    public void ClassDecl_CanHaveNestedEnum()
    {
        var classDecl = CreateClassDecl("NetworkClient");
        classDecl.Types.Add(CreateEnumDecl("State", moduleName: "TestModule.NetworkClient"));

        Assert.Single(classDecl.Types);
    }

    #endregion

    #region Property Deduplication Tests (Bug #9)

    [Fact]
    public void ClassDecl_DuplicatePropertyNames_StaticAndInstance_DetectedByNameSet()
    {
        // Bug #9: When a class has both a static and instance property with the same name
        // (e.g., Rabbit.KeySize), the C# emission produces CS0102. The emitter should
        // detect the collision and skip the duplicate.
        var classDecl = CreateClassDecl("Rabbit");
        classDecl.Properties.Add(CreatePropertyDecl("keySize", "Swift.Int", isStatic: true));
        classDecl.Properties.Add(CreatePropertyDecl("keySize", "Swift.Int", isStatic: false));

        // Both properties have the same name — the second should be detected as a duplicate.
        var names = new HashSet<string>();
        foreach (var prop in classDecl.Properties)
        {
            var csName = NameProvider.GetPropertyName(prop.Name, classDecl.Name);
            if (!names.Add(csName))
            {
                // Second add returns false — it's a duplicate.
                Assert.True(true, "Duplicate property correctly detected");
                return;
            }
        }

        Assert.Fail("Should have detected duplicate property name");
    }

    [Fact]
    public void ClassDecl_UniquePropertyNames_NoDuplication()
    {
        var classDecl = CreateClassDecl("Config");
        classDecl.Properties.Add(CreatePropertyDecl("blockSize", "Swift.Int", isStatic: true));
        classDecl.Properties.Add(CreatePropertyDecl("keySize", "Swift.Int", isStatic: false));

        var names = new HashSet<string>();
        var hasDuplicates = false;
        foreach (var prop in classDecl.Properties)
        {
            var csName = NameProvider.GetPropertyName(prop.Name, classDecl.Name);
            if (!names.Add(csName))
            {
                hasDuplicates = true;
                break;
            }
        }

        Assert.False(hasDuplicates);
    }

    #endregion

    #region Operator Tests

    [Fact]
    public void ClassDecl_CanHaveOperators()
    {
        var classDecl = CreateClassDecl("Vector");
        classDecl.Operators.Add(CreateOperatorDecl("+", OperatorKind.Binary));
        classDecl.Operators.Add(CreateOperatorDecl("-", OperatorKind.Binary));

        Assert.Equal(2, classDecl.Operators.Count);
    }

    [Fact]
    public void ClassDecl_WithEqualityOperator_HasExplicitEquality()
    {
        var classDecl = CreateClassDecl("Point");
        classDecl.Operators.Add(CreateOperatorDecl("==", OperatorKind.Binary));

        var hasEquality = classDecl.Operators.Any(o => o.OperatorSymbol == "==");

        Assert.True(hasEquality);
    }

    #endregion

    #region Helper Methods

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

    private static StructDecl CreateStructDecl(string name, string moduleName = "TestModule")
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

    private static PropertyDecl CreatePropertyDecl(string name, string typeName, bool isStatic = false)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(typeName),
            IsStatic = isStatic,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = $"{name}_Get",
                        MangledName = $"$s{name}g",
                        MethodType = isStatic ? MethodType.Static : MethodType.Instance,
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

    private static MethodDecl CreateMethodDecl(
        string name,
        bool isStatic = false,
        bool isConstructor = false,
        bool isAsync = false,
        bool throws = false)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = isStatic ? MethodType.Static : MethodType.Instance,
            IsConstructor = isConstructor,
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
            Throws = throws,
            IsAsync = isAsync,
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
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Vector"),
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
                SwiftTypeSpec = new NamedTypeSpec("TestModule.Vector"),
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
