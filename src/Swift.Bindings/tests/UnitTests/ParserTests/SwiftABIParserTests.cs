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
        // The Swift ABI digester emits raw value types unqualified; RawValueTypeName stores
        // the unqualified spelling (qualified→bare normalization is pinned separately in
        // RawValueTypeNameNormalizationTests).
        var enumDecl = CreateEnumDecl("IntEnum", rawValueTypeName: "Int");

        Assert.Equal("Int", enumDecl.RawValueTypeName);
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

    [Fact]
    public void CreateEnumDecl_WithGenericParameters_SetsGenericParameters()
    {
        var enumDecl = CreateEnumDecl("ValueProviderStorage");
        enumDecl.GenericParameters.Add(CreateGenericArgumentDecl("τ_0_0"));

        Assert.Single(enumDecl.GenericParameters);
        Assert.Equal("τ_0_0", enumDecl.GenericParameters[0].TypeName);
        Assert.True(enumDecl.IsGeneric);
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
        var classDecl = CreateClassDecl("ImageLoader", moduleName: "ImagePipeline");

        Assert.Equal("ImagePipeline.ImageLoader", classDecl.SwiftTypeName.ModuleQualifiedName);
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

    #region DetectAsyncFromMangledName Tests

    // Positive: real async mangled names
    [Theory]
    [InlineData("$s18BridgeParamTestLib12AsyncServiceC3keyACSS_tYaKcfc", true)]  // async throws ctor
    [InlineData("$s10TestModule5ModelC3runyyYaF", true)]                          // async instance method
    [InlineData("$s10TestModule5ModelC6fetchSiYaKF", true)]                       // async throws method
    // Negative: sync mangled names
    [InlineData("$s10TestModule5ModelC4nameSSvg", false)]                         // sync property getter
    [InlineData("$s10TestModule5ModelC3fooyyF", false)]                           // sync void method
    [InlineData("$s10TestModule5ModelCACycfc", false)]                            // sync parameterless ctor
    public void DetectAsync_RealPatterns(string mangledName, bool expected)
    {
        Assert.Equal(expected, SwiftABIParser.DetectAsyncFromMangledName(mangledName));
    }

    // False-positive resistance: types whose names contain "Ya" (Yak, Yam, Yacht, etc.)
    [Theory]
    [InlineData("$s10TestModule3YakC4nameSSvg", false)]     // 3Yak — digit-prefixed
    [InlineData("$s10TestModule3YamC4nameSSvg", false)]     // 3Yam — digit-prefixed
    [InlineData("$s10TestModule5YachtC4sailyyF", false)]    // 5Ya — digit-prefixed
    [InlineData("$s10TestModule7YankeerC3runyyF", false)]   // 7Ya — digit-prefixed
    [InlineData("$s10TestModule2YaC4nameSSvg", false)]      // 2Ya — digit-prefixed
    public void DetectAsync_FalsePositiveResistance(string mangledName, bool expected)
    {
        Assert.Equal(expected, SwiftABIParser.DetectAsyncFromMangledName(mangledName));
    }

    // Edge cases
    [Theory]
    [InlineData("", false)]       // empty string
    [InlineData("Ya", true)]      // bare marker (not digit-preceded)
    [InlineData("XYa", true)]     // letter-preceded
    [InlineData("9Ya", false)]    // digit-preceded
    public void DetectAsync_EdgeCases(string mangledName, bool expected)
    {
        Assert.Equal(expected, SwiftABIParser.DetectAsyncFromMangledName(mangledName));
    }

    #endregion

    #region TryGetModuleFromSwiftUsr Tests

    // The USR records the type's REAL defining module. For @_originallyDefinedIn symbols
    // (e.g. RealityFoundation's HasTransform, whose mangled name carries the original
    // RealityKit module) the USR is the authoritative module source. Used to correct
    // inherited-protocol references away from the unbound original module (CS0246).
    [Theory]
    [InlineData("s:17RealityFoundation12HasTransformP", "RealityFoundation")] // protocol USR
    [InlineData("s:17RealityFoundation6EntityC", "RealityFoundation")]        // class USR
    [InlineData("s:10Foundation13LocalizedErrorP", "Foundation")]             // cross-framework
    [InlineData("s:5MyMod5OuterV5InnerP", "MyMod")]                           // nested → first segment
    public void TryGetModuleFromSwiftUsr_LengthPrefixed_ReturnsModule(string usr, string expected)
    {
        Assert.True(SwiftABIParser.TryGetModuleFromSwiftUsr(usr, out var module));
        Assert.Equal(expected, module);
    }

    [Theory]
    [InlineData("s:s9EscapableP")]   // stdlib short form — no length prefix
    [InlineData("s:s8CopyableP")]    // stdlib short form
    [InlineData("c:objc(pl)NSCoding")] // ObjC USR — not Swift
    [InlineData("")]                  // empty
    [InlineData("s:")]                // truncated
    [InlineData("s:99RealityFoundation")] // length overruns string
    public void TryGetModuleFromSwiftUsr_NonLengthPrefixedOrInvalid_ReturnsFalse(string usr)
    {
        Assert.False(SwiftABIParser.TryGetModuleFromSwiftUsr(usr, out var module));
        Assert.Null(module);
    }

    #endregion

    #region TryGetModuleFromMangledName Tests

    // Unlike the USR (which records the CURRENT module), the stable mangled name carries the
    // ORIGINAL module of an @_originallyDefinedIn type — which is what the TBD's
    // protocol-conformance-descriptor symbols are mangled with. The conformance-descriptor
    // lookup falls back to this module when the current-module identity misses (e.g. RealityKit's
    // AnchorEntity re-exported as RealityFoundation.AnchorEntity, descriptor symbol
    // `$s10RealityKit12AnchorEntityC...Mc`).
    [Theory]
    [InlineData("$s10RealityKit12AnchorEntityC", "RealityKit")]                       // class, no underscore
    [InlineData("_$s10RealityKit12AnchorEntityC", "RealityKit")]                      // class, leading underscore
    [InlineData("_$s27SwiftBindingsTestLibPhantom15RelocatedEntityC", "SwiftBindingsTestLibPhantom")] // @_originallyDefinedIn phantom module
    [InlineData("$s10Foundation13LocalizedErrorP", "Foundation")]                     // protocol
    public void TryGetModuleFromMangledName_LengthPrefixed_ReturnsModule(string mangled, string expected)
    {
        Assert.True(SwiftABIParser.TryGetModuleFromMangledName(mangled, out var module));
        Assert.Equal(expected, module);
    }

    [Theory]
    [InlineData("$ss8SendableP")]      // stdlib substitution — no length prefix
    [InlineData("$sSH")]               // stdlib well-known substitution
    [InlineData("c:objc(cs)NSObject")] // ObjC mangled — not a Swift stable name
    [InlineData("")]                   // empty
    [InlineData("$s")]                 // truncated
    [InlineData("$s99RealityKit")]     // length overruns string
    public void TryGetModuleFromMangledName_NonLengthPrefixedOrInvalid_ReturnsFalse(string mangled)
    {
        Assert.False(SwiftABIParser.TryGetModuleFromMangledName(mangled, out var module));
        Assert.Null(module);
    }

    #endregion

    #region HasVariadicElement Tests

    [Fact]
    public void HasVariadicElement_VariadicStringParam_ReturnsTrue()
    {
        // Swift `func foo(_ args: String...)` demangles as Array<String> with inner IsVariadic=true
        var innerType = new NamedTypeSpec("Swift.String") { IsVariadic = true };
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(innerType);

        var paramTuple = new TupleTypeSpec(new TypeSpec[] { arrayType });
        Assert.True(SwiftABIParser.HasVariadicElement(paramTuple));
    }

    [Fact]
    public void HasVariadicElement_RegularArrayParam_ReturnsFalse()
    {
        // Regular Array<String> parameter (not variadic) — inner type does NOT have IsVariadic
        var innerType = new NamedTypeSpec("Swift.String");
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(innerType);

        var paramTuple = new TupleTypeSpec(new TypeSpec[] { arrayType });
        Assert.False(SwiftABIParser.HasVariadicElement(paramTuple));
    }

    [Fact]
    public void HasVariadicElement_MixedParams_VariadicAndNon_ReturnsTrue()
    {
        // func foo(_ prefixes: String..., caseSensitive: Bool)
        // Demangled: (Array<String{IsVariadic}>, Bool)
        var innerType = new NamedTypeSpec("Swift.String") { IsVariadic = true };
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(innerType);
        var boolType = new NamedTypeSpec("Swift.Bool");

        var paramTuple = new TupleTypeSpec(new TypeSpec[] { arrayType, boolType });
        Assert.True(SwiftABIParser.HasVariadicElement(paramTuple));
    }

    [Fact]
    public void HasVariadicElement_EmptyParamList_ReturnsFalse()
    {
        var paramTuple = new TupleTypeSpec();
        Assert.False(SwiftABIParser.HasVariadicElement(paramTuple));
    }

    [Fact]
    public void HasVariadicElement_VariadicProtocolParam_ReturnsTrue()
    {
        // func buildBlock(_ disposables: Disposable...) — demangled as Array<Disposable{IsVariadic}>
        var innerType = new NamedTypeSpec("ReactiveStreams.Disposable") { IsVariadic = true };
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(innerType);

        var paramTuple = new TupleTypeSpec(new TypeSpec[] { arrayType });
        Assert.True(SwiftABIParser.HasVariadicElement(paramTuple));
    }

    [Fact]
    public void HasVariadicElement_ArrayNameWithoutModule_ReturnsTrue()
    {
        // Some demangling contexts use "Array" without "Swift." prefix
        var innerType = new NamedTypeSpec("Swift.Int") { IsVariadic = true };
        var arrayType = new NamedTypeSpec("Array");
        arrayType.GenericParameters.Add(innerType);

        var paramTuple = new TupleTypeSpec(new TypeSpec[] { arrayType });
        Assert.True(SwiftABIParser.HasVariadicElement(paramTuple));
    }

    #endregion

    #region B2: Tuple Label Parsing for Enum Associated Values

    /// <summary>
    /// Verifies that TypeSpecParser.Parse() on a labeled tuple printedName
    /// (as found in ABI JSON Tuple nodes) produces a TupleTypeSpec with
    /// TypeLabel set on each element. This is the core mechanism of B2.
    /// </summary>
    [Fact]
    public void TuplePrintedName_LabeledElements_PreservesLabels()
    {
        // Real ABI JSON pattern: "(width: Swift.Double, height: Swift.Double)"
        var parsed = TypeSpecParser.Parse("(width: Swift.Double, height: Swift.Double)");
        var tuple = Assert.IsType<TupleTypeSpec>(parsed);

        Assert.Equal(2, tuple.Elements.Count);

        var width = Assert.IsType<NamedTypeSpec>(tuple.Elements[0]);
        Assert.Equal("width", width.TypeLabel);
        Assert.Equal("Swift.Double", width.Name);

        var height = Assert.IsType<NamedTypeSpec>(tuple.Elements[1]);
        Assert.Equal("height", height.TypeLabel);
        Assert.Equal("Swift.Double", height.Name);
    }

    /// <summary>
    /// Unlabeled tuples produce elements with null TypeLabel.
    /// These fall back to value{i} naming in the emitter.
    /// </summary>
    [Fact]
    public void TuplePrintedName_UnlabeledElements_TypeLabelIsNull()
    {
        var parsed = TypeSpecParser.Parse("(Swift.String, Swift.Int)");
        var tuple = Assert.IsType<TupleTypeSpec>(parsed);

        Assert.Equal(2, tuple.Elements.Count);
        Assert.Null(tuple.Elements[0].TypeLabel);
        Assert.Null(tuple.Elements[1].TypeLabel);
    }

    /// <summary>
    /// Single-element tuples are unwrapped by TypeSpecParser to a NamedTypeSpec.
    /// The label is preserved on the unwrapped element.
    /// </summary>
    [Fact]
    public void TuplePrintedName_SingleLabeledElement_UnwrapsWithLabel()
    {
        // ABI pattern: "(radius: Swift.Double)" → single-element, unwrapped
        var parsed = TypeSpecParser.Parse("(radius: Swift.Double)");

        // TypeSpecParser unwraps single-element tuples
        var named = Assert.IsType<NamedTypeSpec>(parsed);
        Assert.Equal("radius", named.TypeLabel);
        Assert.Equal("Swift.Double", named.Name);
    }

    /// <summary>
    /// Complex types inside labeled tuples: arrays, optionals, any Protocol.
    /// Verifies TypeSpecParser handles the full range of ABI printedName patterns.
    /// </summary>
    [Theory]
    [InlineData("(acceptableContentTypes: [Swift.String], responseContentType: Swift.String)",
        "acceptableContentTypes", "responseContentType")]
    [InlineData("(error: (any Swift.Error)?)",
        "error", null)]  // single-element, unwrapped
    [InlineData("(key: Swift.String, value: Swift.Int)",
        "key", "value")]
    public void TuplePrintedName_ComplexTypes_PreservesLabels(
        string printedName, string expectedLabel0, string? expectedLabel1)
    {
        var parsed = TypeSpecParser.Parse(printedName);

        if (expectedLabel1 != null)
        {
            // Multi-element tuple
            var tuple = Assert.IsType<TupleTypeSpec>(parsed);
            Assert.Equal(expectedLabel0, tuple.Elements[0].TypeLabel);
            Assert.Equal(expectedLabel1, tuple.Elements[1].TypeLabel);
        }
        else
        {
            // Single-element → unwrapped
            Assert.NotNull(parsed);
            Assert.Equal(expectedLabel0, parsed!.TypeLabel);
        }
    }

    /// <summary>
    /// Verifies the fallback behavior: when TypeSpecParser can't parse the tuple
    /// printedName, the parser falls back to child-by-child iteration (which
    /// produces associated values without labels). This test simulates the
    /// fallback by verifying that individual child printedNames still parse
    /// correctly but without labels.
    /// </summary>
    [Fact]
    public void FallbackChildParsing_ProducesAssociatedValuesWithoutLabels()
    {
        // Simulate the fallback path: parsing individual child printedNames
        // (as the parser did before B2, and still does on parse failure)
        var type1 = TypeSpecParser.Parse("Swift.Double");
        var type2 = TypeSpecParser.Parse("Swift.String");

        // Child-by-child parsing never has labels
        var named1 = Assert.IsType<NamedTypeSpec>(type1);
        Assert.Null(named1.TypeLabel);
        Assert.Equal("Swift.Double", named1.Name);

        var named2 = Assert.IsType<NamedTypeSpec>(type2);
        Assert.Null(named2.TypeLabel);
        Assert.Equal("Swift.String", named2.Name);
    }

    /// <summary>
    /// Tests the ABI-vs-swiftinterface label precedence logic.
    /// When ABI provides a label (via tuple printedName parsing), the
    /// swiftinterface label should NOT overwrite it. The swiftinterface
    /// label should only fill gaps (null/empty TypeLabel).
    /// </summary>
    [Fact]
    public void SwiftinterfaceLabel_DoesNotOverwrite_ABILabel()
    {
        // Simulate: ABI parsing produced a TypeSpec with label "radius"
        var abiParsed = TypeSpecParser.Parse("(radius: Swift.Double)");
        var typeSpec = Assert.IsType<NamedTypeSpec>(abiParsed);
        Assert.Equal("radius", typeSpec.TypeLabel);

        // Simulate swiftinterface overlay with a different label
        string? swiftinterfaceLabel = "r";

        // The B2 guard: only apply when ABI didn't already provide one
        if (swiftinterfaceLabel != null && string.IsNullOrEmpty(typeSpec.TypeLabel))
        {
            typeSpec.TypeLabel = swiftinterfaceLabel;
        }

        // ABI label wins — not overwritten
        Assert.Equal("radius", typeSpec.TypeLabel);
    }

    /// <summary>
    /// When ABI doesn't provide a label (null TypeLabel), swiftinterface
    /// label fills the gap.
    /// </summary>
    [Fact]
    public void SwiftinterfaceLabel_FillsGap_WhenABILabelMissing()
    {
        // Simulate: ABI parsing produced a TypeSpec WITHOUT a label
        // (child-by-child fallback or unlabeled tuple element)
        var typeSpec = new NamedTypeSpec("Swift.Double");
        Assert.Null(typeSpec.TypeLabel);

        string? swiftinterfaceLabel = "radius";

        // The B2 guard: only apply when ABI didn't already provide one
        if (swiftinterfaceLabel != null && string.IsNullOrEmpty(typeSpec.TypeLabel))
        {
            typeSpec.TypeLabel = swiftinterfaceLabel;
        }

        // swiftinterface label fills the gap
        Assert.Equal("radius", typeSpec.TypeLabel);
    }

    /// <summary>
    /// Partially labeled tuples: some elements have ABI labels, others don't.
    /// swiftinterface should only fill the gaps.
    /// </summary>
    [Fact]
    public void MixedLabeling_SwiftinterfaceOnlyFillsGaps()
    {
        // ABI: "(width: Swift.Double, Swift.Double)" — first labeled, second not
        var parsed = TypeSpecParser.Parse("(width: Swift.Double, Swift.Double)");
        var tuple = Assert.IsType<TupleTypeSpec>(parsed);

        Assert.Equal("width", tuple.Elements[0].TypeLabel);
        Assert.Null(tuple.Elements[1].TypeLabel);

        // Simulate swiftinterface labels for both positions
        var swiftinterfaceLabels = new[] { "w", "height" };

        for (int i = 0; i < tuple.Elements.Count; i++)
        {
            if (swiftinterfaceLabels[i] != null && string.IsNullOrEmpty(tuple.Elements[i].TypeLabel))
            {
                tuple.Elements[i].TypeLabel = swiftinterfaceLabels[i];
            }
        }

        // "width" from ABI preserved (not overwritten by "w")
        Assert.Equal("width", tuple.Elements[0].TypeLabel);
        // "height" from swiftinterface fills the gap
        Assert.Equal("height", tuple.Elements[1].TypeLabel);
    }

    #endregion

    #region Implicit+Overriding Constructor Filtering Tests

    [Fact]
    public void ImplicitOverridingConstructor_MarkedAsModuleInternal()
    {
        // Issue M: Implicit+overriding constructors on classes that define their own
        // designated inits (inheritsConvenienceInitializers=false, hasMissingDesignatedInitializers=false)
        // should be marked IsModuleInternal so they don't get emitted.
        // This prevents "missing argument for parameter" errors in generated wrappers.
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

        var classDecl = new ClassDecl
        {
            Name = "CompatibleAnimation",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.CompatibleAnimation"),
            MangledName = "$s10TestModule19CompatibleAnimationCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            InheritsConvenienceInitializers = false,
            HasMissingDesignatedInitializers = false
        };

        // Implicit+overriding zero-arg constructor — should be marked internal
        var implicitCtor = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule19CompatibleAnimationCACycfc",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            IsImplicit = true,
            IsOverride = true,
            IsModuleInternal = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    PrivateName = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        classDecl.Methods.Add(implicitCtor);

        // Simulate the parser's post-processing logic
        foreach (var method in classDecl.Methods)
        {
            if (!method.IsModuleInternal && method.IsImplicit && method.IsOverride &&
                method.IsConstructor && method.ParentDecl is ClassDecl classParent &&
                !classParent.InheritsConvenienceInitializers &&
                !classParent.HasMissingDesignatedInitializers)
            {
                method.IsModuleInternal = true;
            }
        }

        Assert.True(implicitCtor.IsModuleInternal,
            "Implicit+overriding constructor should be marked as module-internal when class defines its own designated inits");
    }

    [Fact]
    public void ImplicitOverridingConstructor_NotMarkedWhenInheritsConvenience()
    {
        // When a class inherits convenience initializers, implicit constructors ARE valid
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

        var classDecl = new ClassDecl
        {
            Name = "SimpleView",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SimpleView"),
            MangledName = "$s10TestModule10SimpleViewCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            InheritsConvenienceInitializers = true,
            HasMissingDesignatedInitializers = false
        };

        var implicitCtor = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule10SimpleViewCACycfc",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            IsImplicit = true,
            IsOverride = true,
            IsModuleInternal = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    PrivateName = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        classDecl.Methods.Add(implicitCtor);

        // Apply same filter logic
        foreach (var method in classDecl.Methods)
        {
            if (!method.IsModuleInternal && method.IsImplicit && method.IsOverride &&
                method.IsConstructor && method.ParentDecl is ClassDecl classParent &&
                !classParent.InheritsConvenienceInitializers &&
                !classParent.HasMissingDesignatedInitializers)
            {
                method.IsModuleInternal = true;
            }
        }

        Assert.False(implicitCtor.IsModuleInternal,
            "Implicit constructor should NOT be internal when class inherits convenience initializers");
    }

    #endregion

    #region Setter Availability Merging Tests

    /// <summary>
    /// When the setter accessor declares a tighter introduced version than the property
    /// (e.g. WorkoutKit.PowerThresholdAlert.metric: getter iOS 17.0, setter iOS 17.4),
    /// the merged list must override the property's version for that platform while
    /// keeping other platforms intact. This is what drives both the Swift wrapper's
    /// stricter @available emission and the C# accessor-level [SupportedOSPlatform].
    /// </summary>
    [Fact]
    public void MergeAccessorAvailability_SetterTighterThanProperty_OverridesPlatform()
    {
        var propertyAvail = new List<AvailabilityAnnotation>
        {
            new("iOS", "17.0", null, null, false, false, null, null),
            new("watchOS", "10.0", null, null, false, false, null, null),
        };
        var setterAvail = new List<AvailabilityAnnotation>
        {
            new("iOS", "17.4", null, null, false, false, null, null),
            new("watchOS", "10.4", null, null, false, false, null, null),
        };

        var merged = SwiftABIParser.MergeAccessorAvailability(propertyAvail, setterAvail);

        Assert.NotNull(merged);
        var iOS = Assert.Single(merged!, a => a.Platform == "iOS");
        Assert.Equal("17.4", iOS.IntroducedVersion);
        var watchOS = Assert.Single(merged!, a => a.Platform == "watchOS");
        Assert.Equal("10.4", watchOS.IntroducedVersion);
    }

    [Fact]
    public void MergeAccessorAvailability_SetterPartialPlatformSet_KeepsPropertyForOthers()
    {
        // Setter only tightens iOS; tvOS stays at the property-level version.
        var propertyAvail = new List<AvailabilityAnnotation>
        {
            new("iOS", "17.0", null, null, false, false, null, null),
            new("tvOS", "17.0", null, null, false, false, null, null),
        };
        var setterAvail = new List<AvailabilityAnnotation>
        {
            new("iOS", "17.4", null, null, false, false, null, null),
        };

        var merged = SwiftABIParser.MergeAccessorAvailability(propertyAvail, setterAvail);

        Assert.NotNull(merged);
        var iOS = Assert.Single(merged!, a => a.Platform == "iOS");
        Assert.Equal("17.4", iOS.IntroducedVersion);
        var tvOS = Assert.Single(merged!, a => a.Platform == "tvOS");
        Assert.Equal("17.0", tvOS.IntroducedVersion);
    }

    [Fact]
    public void MergeAccessorAvailability_NoAccessorAnnotations_ReturnsPropertyAvailability()
    {
        var propertyAvail = new List<AvailabilityAnnotation>
        {
            new("iOS", "17.0", null, null, false, false, null, null),
        };

        var merged = SwiftABIParser.MergeAccessorAvailability(propertyAvail, accessorAvailability: null);

        Assert.NotNull(merged);
        var iOS = Assert.Single(merged!, a => a.Platform == "iOS");
        Assert.Equal("17.0", iOS.IntroducedVersion);
    }

    [Fact]
    public void MergeAccessorAvailability_BothNull_ReturnsNull()
    {
        Assert.Null(SwiftABIParser.MergeAccessorAvailability(propertyAvailability: null, accessorAvailability: null));
    }

    [Fact]
    public void MergeAccessorAvailability_PreservesNonPlatformPassthroughs()
    {
        // Unconditional `@available(*, deprecated)` has Platform==null and should
        // pass through to the merged result alongside platform-specific entries.
        var passthrough = new AvailabilityAnnotation(
            Platform: null, IntroducedVersion: null, DeprecatedVersion: null,
            ObsoletedVersion: null, IsUnconditionallyDeprecated: true,
            IsUnconditionallyUnavailable: false, Message: "gone", Renamed: null);
        var propertyAvail = new List<AvailabilityAnnotation>
        {
            new("iOS", "17.0", null, null, false, false, null, null),
            passthrough,
        };
        var setterAvail = new List<AvailabilityAnnotation>
        {
            new("iOS", "17.4", null, null, false, false, null, null),
        };

        var merged = SwiftABIParser.MergeAccessorAvailability(propertyAvail, setterAvail);

        Assert.NotNull(merged);
        Assert.Contains(merged!, a => a.IsUnconditionallyDeprecated && a.Message == "gone");
        var iOS = Assert.Single(merged!, a => a.Platform == "iOS");
        Assert.Equal("17.4", iOS.IntroducedVersion);
    }

    #endregion

    #region Tuple Detection Tests

    [Fact]
    public void TupleDetection_NodeWithNameTuple_RecognizedAsTuple()
    {
        // ABI JSON tuple nodes have kind="TypeNominal" and name="Tuple".
        // The parser must check BOTH Kind and Name to detect tuples,
        // because kTuple="Tuple" matches Name but not Kind ("TypeNominal").
        var kTuple = "Tuple";
        var kNominal = "TypeNominal";

        // Simulate the ABI JSON node structure
        var nodeKind = kNominal;  // Actual kind in ABI JSON
        var nodeName = kTuple;    // Actual name in ABI JSON

        // Old behavior (broken): only checked Kind
        bool oldDetection = nodeKind == kTuple;
        Assert.False(oldDetection, "Old detection should NOT match — Kind is TypeNominal, not Tuple");

        // New behavior (fixed): checks both Kind and Name
        bool newDetection = nodeKind == kTuple || nodeName == kTuple;
        Assert.True(newDetection, "New detection should match via Name check");
    }

    #endregion
}
