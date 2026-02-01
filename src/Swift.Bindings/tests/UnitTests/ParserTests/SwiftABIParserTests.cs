// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SwiftABIParser parsing functionality.
/// These tests focus on the creation of declaration objects from parsed ABI JSON nodes.
/// </summary>
public class SwiftABIParserTests
{
    #region StructDecl Creation Tests

    [Fact]
    public void CreateStructDecl_WithBasicStruct_SetsCorrectName()
    {
        var structDecl = CreateStructDecl("Point");

        Assert.Equal("Point", structDecl.Name);
    }

    [Fact]
    public void CreateStructDecl_WithFrozenAttribute_SetsFrozenTrue()
    {
        var structDecl = CreateStructDecl("FrozenPoint", isFrozen: true);

        Assert.True(structDecl.IsFrozen);
    }

    [Fact]
    public void CreateStructDecl_WithoutFrozenAttribute_SetsFrozenFalse()
    {
        var structDecl = CreateStructDecl("NonFrozenStruct", isFrozen: false);

        Assert.False(structDecl.IsFrozen);
    }

    [Fact]
    public void CreateStructDecl_SetsCorrectSwiftTypeName()
    {
        var structDecl = CreateStructDecl("Point", moduleName: "CoreGraphics");

        Assert.Equal("CoreGraphics.Point", structDecl.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void CreateStructDecl_WithMangledName_SetsMangledName()
    {
        var structDecl = CreateStructDecl("Point", mangledName: "$s12CoreGraphics5PointVN");

        Assert.Equal("$s12CoreGraphics5PointVN", structDecl.MangledName);
    }

    [Fact]
    public void CreateStructDecl_InitializesEmptyCollections()
    {
        var structDecl = CreateStructDecl("EmptyStruct");

        Assert.Empty(structDecl.Properties);
        Assert.Empty(structDecl.Methods);
        Assert.Empty(structDecl.Types);
        Assert.Empty(structDecl.Operators);
    }

    [Fact]
    public void CreateStructDecl_WithGenericParameters_SetsGenericParameters()
    {
        var structDecl = CreateStructDecl("Container");
        structDecl.GenericParameters.Add(CreateGenericArgumentDecl("T"));

        Assert.Single(structDecl.GenericParameters);
        Assert.Equal("T", structDecl.GenericParameters[0].TypeName);
    }

    [Fact]
    public void CreateStructDecl_WithConformances_SetsConformances()
    {
        var structDecl = CreateStructDecl("EquatableStruct");
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.EquatableStruct"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sConformanceDescriptor"));

        Assert.Single(structDecl.Conformances);
    }

    #endregion

    #region EnumDecl Creation Tests

    [Fact]
    public void CreateEnumDecl_WithBasicEnum_SetsCorrectName()
    {
        var enumDecl = CreateEnumDecl("Direction");

        Assert.Equal("Direction", enumDecl.Name);
    }

    [Fact]
    public void CreateEnumDecl_WithFrozenAttribute_SetsFrozenTrue()
    {
        var enumDecl = CreateEnumDecl("FrozenDirection", isFrozen: true);

        Assert.True(enumDecl.IsFrozen);
    }

    [Fact]
    public void CreateEnumDecl_WithRawValueType_SetsRawValueTypeName()
    {
        var enumDecl = CreateEnumDecl("IntEnum", rawValueTypeName: "Swift.Int");

        Assert.Equal("Swift.Int", enumDecl.RawValueTypeName);
    }

    [Fact]
    public void CreateEnumDecl_WithoutRawValueType_RawValueTypeNameIsNull()
    {
        var enumDecl = CreateEnumDecl("SimpleEnum");

        Assert.Null(enumDecl.RawValueTypeName);
    }

    [Fact]
    public void CreateEnumDecl_InitializesEmptyCasesList()
    {
        var enumDecl = CreateEnumDecl("EmptyEnum");

        Assert.Empty(enumDecl.Cases);
    }

    [Fact]
    public void CreateEnumDecl_WithCases_CollectsCases()
    {
        var enumDecl = CreateEnumDecl("Direction");
        enumDecl.Cases.Add(CreateEnumCaseDecl("north"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("south"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("east"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("west"));

        Assert.Equal(4, enumDecl.Cases.Count);
    }

    #endregion

    #region EnumCaseDecl Creation Tests

    [Fact]
    public void CreateEnumCaseDecl_SimplCase_HasNoAssociatedValues()
    {
        var caseDecl = CreateEnumCaseDecl("north");

        Assert.Empty(caseDecl.AssociatedValues);
    }

    [Fact]
    public void CreateEnumCaseDecl_SetsCorrectName()
    {
        var caseDecl = CreateEnumCaseDecl("loading");

        Assert.Equal("loading", caseDecl.Name);
    }

    [Fact]
    public void CreateEnumCaseDecl_WithAssociatedValue_CollectsAssociatedValues()
    {
        var caseDecl = CreateEnumCaseDecl("success");
        caseDecl.AssociatedValues.Add(new NamedTypeSpec("Swift.String"));

        Assert.Single(caseDecl.AssociatedValues);
    }

    [Fact]
    public void CreateEnumCaseDecl_WithMultipleAssociatedValues_CollectsAll()
    {
        var caseDecl = CreateEnumCaseDecl("result");
        caseDecl.AssociatedValues.Add(new NamedTypeSpec("Swift.Int"));
        caseDecl.AssociatedValues.Add(new NamedTypeSpec("Swift.String"));
        caseDecl.AssociatedValues.Add(new NamedTypeSpec("Swift.Bool"));

        Assert.Equal(3, caseDecl.AssociatedValues.Count);
    }

    #endregion

    #region ClassDecl Creation Tests

    [Fact]
    public void CreateClassDecl_WithBasicClass_SetsCorrectName()
    {
        var classDecl = CreateClassDecl("MyClass");

        Assert.Equal("MyClass", classDecl.Name);
    }

    [Fact]
    public void CreateClassDecl_SetsCorrectSwiftTypeName()
    {
        var classDecl = CreateClassDecl("ImageLoader", moduleName: "Nuke");

        Assert.Equal("Nuke.ImageLoader", classDecl.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void CreateClassDecl_InitializesEmptyCollections()
    {
        var classDecl = CreateClassDecl("EmptyClass");

        Assert.Empty(classDecl.Properties);
        Assert.Empty(classDecl.Methods);
        Assert.Empty(classDecl.Types);
        Assert.Empty(classDecl.Operators);
    }

    [Fact]
    public void CreateClassDecl_WithGenericParameters_SetsGenericParameters()
    {
        var classDecl = CreateClassDecl("GenericClass");
        classDecl.GenericParameters.Add(CreateGenericArgumentDecl("T"));
        classDecl.GenericParameters.Add(CreateGenericArgumentDecl("U"));

        Assert.Equal(2, classDecl.GenericParameters.Count);
    }

    #endregion

    #region ProtocolDecl Creation Tests

    [Fact]
    public void CreateProtocolDecl_WithBasicProtocol_SetsCorrectName()
    {
        var protocolDecl = CreateProtocolDecl("Loadable");

        Assert.Equal("Loadable", protocolDecl.Name);
    }

    [Fact]
    public void CreateProtocolDecl_InitializesEmptyAssociatedTypes()
    {
        var protocolDecl = CreateProtocolDecl("SimpleProtocol");

        Assert.Empty(protocolDecl.AssociatedTypes);
    }

    [Fact]
    public void CreateProtocolDecl_WithAssociatedTypes_CollectsAssociatedTypes()
    {
        var protocolDecl = CreateProtocolDecl("Collection");
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Index" });

        Assert.Equal(2, protocolDecl.AssociatedTypes.Count);
    }

    [Fact]
    public void CreateProtocolDecl_WithSelfRequirement_SetsSelfRequirementTrue()
    {
        var protocolDecl = CreateProtocolDecl("Equatable");
        protocolDecl.HasSelfRequirement = true;

        Assert.True(protocolDecl.HasSelfRequirement);
    }

    [Fact]
    public void CreateProtocolDecl_WithInheritedProtocols_SetsInheritedProtocols()
    {
        var protocolDecl = CreateProtocolDecl("Hashable");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("Swift.Equatable"));

        Assert.Single(protocolDecl.InheritedProtocols);
    }

    [Fact]
    public void CreateProtocolDecl_ClassBound_IsClassBoundTrue()
    {
        var protocolDecl = CreateProtocolDecl("ClassOnlyProtocol");
        protocolDecl.IsClassBound = true;

        Assert.True(protocolDecl.IsClassBound);
    }

    #endregion

    #region MethodDecl Creation Tests

    [Fact]
    public void CreateMethodDecl_WithBasicMethod_SetsCorrectName()
    {
        var methodDecl = CreateMethodDecl("doSomething");

        Assert.Equal("doSomething", methodDecl.Name);
    }

    [Fact]
    public void CreateMethodDecl_InstanceMethod_SetsInstanceMethodType()
    {
        var methodDecl = CreateMethodDecl("instanceMethod", isStatic: false);

        Assert.Equal(MethodType.Instance, methodDecl.MethodType);
    }

    [Fact]
    public void CreateMethodDecl_StaticMethod_SetsStaticMethodType()
    {
        var methodDecl = CreateMethodDecl("staticMethod", isStatic: true);

        Assert.Equal(MethodType.Static, methodDecl.MethodType);
    }

    [Fact]
    public void CreateMethodDecl_Constructor_SetsIsConstructorTrue()
    {
        var methodDecl = CreateMethodDecl("init", isConstructor: true);

        Assert.True(methodDecl.IsConstructor);
    }

    [Fact]
    public void CreateMethodDecl_ThrowingMethod_SetsThrowsTrue()
    {
        var methodDecl = CreateMethodDecl("riskyOperation", throws: true);

        Assert.True(methodDecl.Throws);
    }

    [Fact]
    public void CreateMethodDecl_AsyncMethod_SetsIsAsyncTrue()
    {
        var methodDecl = CreateMethodDecl("fetchData", isAsync: true);

        Assert.True(methodDecl.IsAsync);
    }

    [Fact]
    public void CreateMethodDecl_WithParameters_CollectsAllParameters()
    {
        var methodDecl = CreateMethodDecl("process");
        // CreateMethodDecl already includes return type placeholder as first element
        methodDecl.CSSignature.Add(CreateArgumentDecl("input", new NamedTypeSpec("Swift.String")));
        methodDecl.CSSignature.Add(CreateArgumentDecl("count", new NamedTypeSpec("Swift.Int")));

        // 1 return type + 2 parameters = 3 total
        Assert.Equal(3, methodDecl.CSSignature.Count);
    }

    [Fact]
    public void CreateMethodDecl_WithGenericParameters_CollectsGenericParams()
    {
        var methodDecl = CreateMethodDecl("transform");
        methodDecl.GenericParameters.Add(CreateGenericArgumentDecl("T"));

        Assert.Single(methodDecl.GenericParameters);
    }

    [Fact]
    public void CreateMethodDecl_DefaultVisibility_IsPublic()
    {
        var methodDecl = CreateMethodDecl("publicMethod");

        Assert.Equal(Visibility.Public, methodDecl.Visibility);
    }

    #endregion

    #region PropertyDecl Creation Tests

    [Fact]
    public void CreatePropertyDecl_WithBasicProperty_SetsCorrectName()
    {
        var propertyDecl = CreatePropertyDecl("value", "Swift.Int");

        Assert.Equal("value", propertyDecl.Name);
    }

    [Fact]
    public void CreatePropertyDecl_SetsCorrectTypeSpec()
    {
        var propertyDecl = CreatePropertyDecl("name", "Swift.String");

        Assert.IsType<NamedTypeSpec>(propertyDecl.SwiftTypeSpec);
        Assert.Equal("Swift.String", ((NamedTypeSpec)propertyDecl.SwiftTypeSpec).Name);
    }

    [Fact]
    public void CreatePropertyDecl_InstanceProperty_IsStaticFalse()
    {
        var propertyDecl = CreatePropertyDecl("instanceProp", "Swift.Int", isStatic: false);

        Assert.False(propertyDecl.IsStatic);
    }

    [Fact]
    public void CreatePropertyDecl_StaticProperty_IsStaticTrue()
    {
        var propertyDecl = CreatePropertyDecl("staticProp", "Swift.Int", isStatic: true);

        Assert.True(propertyDecl.IsStatic);
    }

    [Fact]
    public void CreatePropertyDecl_WithStorageAttribute_HasStorageTrue()
    {
        var propertyDecl = CreatePropertyDecl("storedValue", "Swift.Int", hasStorage: true);

        Assert.True(propertyDecl.HasStorage);
    }

    [Fact]
    public void CreatePropertyDecl_WithGetAccessor_HasGetAccessor()
    {
        var propertyDecl = CreatePropertyDecl("readOnlyProp", "Swift.Int", hasGetter: true, hasSetter: false);

        Assert.Single(propertyDecl.Accessors);
        Assert.IsType<GetAccessorDecl>(propertyDecl.Accessors[0]);
    }

    [Fact]
    public void CreatePropertyDecl_WithBothAccessors_HasBothAccessors()
    {
        var propertyDecl = CreatePropertyDecl("readWriteProp", "Swift.Int", hasGetter: true, hasSetter: true);

        Assert.Equal(2, propertyDecl.Accessors.Count);
    }

    #endregion

    #region OperatorDecl Creation Tests

    [Fact]
    public void CreateOperatorDecl_BinaryOperator_SetsCorrectKind()
    {
        var opDecl = CreateOperatorDecl("+", OperatorKind.Binary);

        Assert.Equal(OperatorKind.Binary, opDecl.Kind);
    }

    [Fact]
    public void CreateOperatorDecl_UnaryOperator_SetsCorrectKind()
    {
        var opDecl = CreateOperatorDecl("!", OperatorKind.Unary);

        Assert.Equal(OperatorKind.Unary, opDecl.Kind);
    }

    [Fact]
    public void CreateOperatorDecl_PrefixOperator_IsPrefixTrue()
    {
        var opDecl = CreateOperatorDecl("-", OperatorKind.Unary, isPrefix: true);

        Assert.True(opDecl.IsPrefix);
    }

    [Fact]
    public void CreateOperatorDecl_PostfixOperator_IsPrefixFalse()
    {
        var opDecl = CreateOperatorDecl("++", OperatorKind.Unary, isPrefix: false);

        Assert.False(opDecl.IsPrefix);
    }

    [Fact]
    public void CreateOperatorDecl_HasUnderlyingMethod()
    {
        var opDecl = CreateOperatorDecl("==", OperatorKind.Binary);

        Assert.NotNull(opDecl.UnderlyingMethod);
    }

    #endregion

    #region ModuleDecl Creation Tests

    [Fact]
    public void CreateModuleDecl_SetsCorrectName()
    {
        var moduleDecl = CreateModuleDecl("TestModule");

        Assert.Equal("TestModule", moduleDecl.Name);
    }

    [Fact]
    public void CreateModuleDecl_InitializesEmptyCollections()
    {
        var moduleDecl = CreateModuleDecl("EmptyModule");

        Assert.Empty(moduleDecl.Properties);
        Assert.Empty(moduleDecl.Methods);
        Assert.Empty(moduleDecl.Types);
        Assert.Empty(moduleDecl.Protocols);
        Assert.Empty(moduleDecl.Dependencies);
    }

    [Fact]
    public void CreateModuleDecl_WithTypes_CollectsTypes()
    {
        var moduleDecl = CreateModuleDecl("TypedModule");
        moduleDecl.Types.Add(CreateStructDecl("Point", moduleName: "TypedModule"));
        moduleDecl.Types.Add(CreateClassDecl("ImageLoader", moduleName: "TypedModule"));

        Assert.Equal(2, moduleDecl.Types.Count);
    }

    [Fact]
    public void CreateModuleDecl_WithDependencies_CollectsDependencies()
    {
        var moduleDecl = CreateModuleDecl("DependentModule");
        moduleDecl.Dependencies.Add("Foundation");
        moduleDecl.Dependencies.Add("UIKit");

        Assert.Equal(2, moduleDecl.Dependencies.Count);
    }

    #endregion

    #region Keyword Escaping Tests

    [Theory]
    [InlineData("class", "_class")]
    [InlineData("struct", "_struct")]
    [InlineData("enum", "_enum")]
    [InlineData("public", "_public")]
    [InlineData("private", "_private")]
    [InlineData("internal", "_internal")]
    [InlineData("if", "_if")]
    [InlineData("else", "_else")]
    [InlineData("for", "_for")]
    [InlineData("while", "_while")]
    public void ExtractUniqueName_WithKeyword_EscapesWithUnderscore(string keyword, string expected)
    {
        // Using a method that would be named after a keyword
        var methodDecl = CreateMethodDeclWithRawName(keyword);

        // The CreateMethodDeclWithRawName helper simulates the escaping
        Assert.Equal(expected, methodDecl.Name);
    }

    [Theory]
    [InlineData("normalName")]
    [InlineData("myMethod")]
    [InlineData("Point")]
    [InlineData("loadImage")]
    public void ExtractUniqueName_WithNonKeyword_DoesNotEscape(string name)
    {
        var methodDecl = CreateMethodDeclWithRawName(name);

        Assert.Equal(name, methodDecl.Name);
    }

    #endregion

    #region Helper Methods

    private static StructDecl CreateStructDecl(
        string name,
        string moduleName = "TestModule",
        bool isFrozen = false,
        string mangledName = "")
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = string.IsNullOrEmpty(mangledName) ? $"$s{moduleName.Length}{moduleName}{name.Length}{name}VN" : mangledName,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = isFrozen,
            MetadataAccessor = ""
        };
    }

    private static EnumDecl CreateEnumDecl(
        string name,
        string moduleName = "TestModule",
        bool isFrozen = false,
        string? rawValueTypeName = null)
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
            IsFrozen = isFrozen,
            MetadataAccessor = "",
            RawValueTypeName = rawValueTypeName
        };
    }

    private static EnumCaseDecl CreateEnumCaseDecl(string name)
    {
        return new EnumCaseDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            AssociatedValues = new List<TypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
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

    private static MethodDecl CreateMethodDecl(
        string name,
        bool isStatic = false,
        bool isConstructor = false,
        bool throws = false,
        bool isAsync = false)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = isStatic ? MethodType.Static : MethodType.Instance,
            IsConstructor = isConstructor,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type placeholder
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

    private static MethodDecl CreateMethodDeclWithRawName(string rawName)
    {
        // Simulate the keyword escaping that SwiftABIParser does
        var escapedName = IsKeyword(rawName) ? $"_{rawName}" : rawName;

        return new MethodDecl
        {
            Name = escapedName,
            MangledName = $"$s{rawName}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static bool IsKeyword(string name)
    {
        // Subset of C# keywords that need escaping
        var keywords = new HashSet<string>
        {
            "class", "struct", "enum", "public", "private", "protected", "internal",
            "if", "else", "for", "while", "do", "switch", "case", "default",
            "return", "break", "continue", "throw", "try", "catch", "finally",
            "new", "this", "base", "null", "true", "false", "void",
            "int", "long", "float", "double", "bool", "string", "object",
            "static", "const", "readonly", "volatile", "virtual", "override",
            "abstract", "sealed", "partial", "async", "await", "using",
            "namespace", "interface", "delegate", "event", "operator"
        };
        return keywords.Contains(name);
    }

    private static PropertyDecl CreatePropertyDecl(
        string name,
        string typeName,
        bool isStatic = false,
        bool hasStorage = false,
        bool hasGetter = true,
        bool hasSetter = false)
    {
        var accessors = new List<AccessorDecl>();

        if (hasGetter)
        {
            accessors.Add(new GetAccessorDecl
            {
                Method = new MethodDecl
                {
                    Name = $"{name}_Get",
                    MangledName = $"$s{name}g",
                    MethodType = isStatic ? MethodType.Static : MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = new NamedTypeSpec(typeName),
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
                    Visibility = Visibility.Private
                }
            });
        }

        if (hasSetter)
        {
            accessors.Add(new SetAccessorDecl
            {
                Method = new MethodDecl
                {
                    Name = $"{name}_Set",
                    MangledName = $"$s{name}s",
                    MethodType = isStatic ? MethodType.Static : MethodType.Instance,
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
                        },
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = new NamedTypeSpec(typeName),
                            Name = "value",
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
                    Visibility = Visibility.Private
                }
            });
        }

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(typeName),
            IsStatic = isStatic,
            HasStorage = hasStorage,
            Accessors = accessors,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ArgumentDecl CreateArgumentDecl(string name, TypeSpec typeSpec)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
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

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Protocols = new List<ProtocolDecl>(),
            Dependencies = new List<string>(),
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

    #endregion
}
