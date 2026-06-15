// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ProtocolHandler and ProtocolHandlerFactory.
/// </summary>
public class ProtocolHandlerTests
{
    #region Factory Tests

    [Fact]
    public void Factory_Handles_ProtocolDecl_ReturnsTrue()
    {
        var factory = new ProtocolHandlerFactory(NullLoggerFactory.Instance);
        var protocolDecl = CreateProtocolDecl("Loadable");

        Assert.True(factory.Handles(protocolDecl));
    }

    [Fact]
    public void Factory_Handles_StructDecl_ReturnsFalse()
    {
        var factory = new ProtocolHandlerFactory(NullLoggerFactory.Instance);
        var structDecl = CreateStructDecl("Point");

        Assert.False(factory.Handles(structDecl));
    }

    [Fact]
    public void Factory_Handles_ClassDecl_ReturnsFalse()
    {
        var factory = new ProtocolHandlerFactory(NullLoggerFactory.Instance);
        var classDecl = CreateClassDecl("MyClass");

        Assert.False(factory.Handles(classDecl));
    }

    [Fact]
    public void Factory_Handles_EnumDecl_ReturnsFalse()
    {
        var factory = new ProtocolHandlerFactory(NullLoggerFactory.Instance);
        var enumDecl = CreateEnumDecl("MyEnum");

        Assert.False(factory.Handles(enumDecl));
    }

    [Fact]
    public void Factory_Construct_ReturnsHandler()
    {
        var factory = new ProtocolHandlerFactory(NullLoggerFactory.Instance);

        var handler = factory.Construct();

        Assert.NotNull(handler);
        Assert.IsType<ProtocolHandler>(handler);
    }

    #endregion

    #region ProtocolDecl Configuration Tests

    [Fact]
    public void ProtocolDecl_HasCorrectName()
    {
        var protocolDecl = CreateProtocolDecl("Loadable");

        Assert.Equal("Loadable", protocolDecl.Name);
    }

    [Fact]
    public void ProtocolDecl_HasCorrectSwiftTypeName()
    {
        var protocolDecl = CreateProtocolDecl("Refreshable", moduleName: "MyApp");

        Assert.Equal("MyApp.Refreshable", protocolDecl.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void ProtocolDecl_InitializesEmptyCollections()
    {
        var protocolDecl = CreateProtocolDecl("EmptyProtocol");

        Assert.Empty(protocolDecl.Properties);
        Assert.Empty(protocolDecl.Methods);
        Assert.Empty(protocolDecl.AssociatedTypes);
        Assert.Empty(protocolDecl.InheritedProtocols);
    }

    #endregion

    #region Associated Types Tests

    [Fact]
    public void ProtocolDecl_WithAssociatedType_HasAssociatedTypes()
    {
        var protocolDecl = CreateProtocolDecl("Collection");
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });

        Assert.Single(protocolDecl.AssociatedTypes);
        Assert.Equal("Element", protocolDecl.AssociatedTypes[0].Name);
    }

    [Fact]
    public void ProtocolDecl_WithMultipleAssociatedTypes_CollectsAll()
    {
        var protocolDecl = CreateProtocolDecl("BidirectionalCollection");
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Index" });
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "SubSequence" });

        Assert.Equal(3, protocolDecl.AssociatedTypes.Count);
    }

    [Fact]
    public void ProtocolDecl_AssociatedTypeWithDefaultType_HasDefaultType()
    {
        var protocolDecl = CreateProtocolDecl("IteratorProtocol");
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl
        {
            Name = "Element",
            DefaultType = new NamedTypeSpec("Swift.Int")
        });

        Assert.NotNull(protocolDecl.AssociatedTypes[0].DefaultType);
    }

    [Fact]
    public void ProtocolDecl_AssociatedTypeWithConstraints_HasConstraints()
    {
        var protocolDecl = CreateProtocolDecl("SortedCollection");
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl
        {
            Name = "Element",
            Constraints = new List<string> { "Comparable" }
        });

        Assert.Single(protocolDecl.AssociatedTypes[0].Constraints);
    }

    [Fact]
    public void ProtocolDecl_WithAssociatedTypes_CannotBeExistential()
    {
        var protocolDecl = CreateProtocolDecl("PATProtocol");
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });

        Assert.False(protocolDecl.CanBeExistential);
    }

    #endregion

    #region Self Requirement Tests

    [Fact]
    public void ProtocolDecl_WithSelfRequirement_HasSelfRequirementTrue()
    {
        var protocolDecl = CreateProtocolDecl("Equatable");
        protocolDecl.HasSelfRequirement = true;

        Assert.True(protocolDecl.HasSelfRequirement);
    }

    [Fact]
    public void ProtocolDecl_WithoutSelfRequirement_HasSelfRequirementFalse()
    {
        var protocolDecl = CreateProtocolDecl("CustomStringConvertible");

        Assert.False(protocolDecl.HasSelfRequirement);
    }

    [Fact]
    public void ProtocolDecl_WithSelfRequirement_CannotBeExistential()
    {
        var protocolDecl = CreateProtocolDecl("Equatable");
        protocolDecl.HasSelfRequirement = true;

        Assert.False(protocolDecl.CanBeExistential);
    }

    #endregion

    #region Inherited Protocols Tests

    [Fact]
    public void ProtocolDecl_WithInheritedProtocol_HasInheritedProtocols()
    {
        var protocolDecl = CreateProtocolDecl("Hashable");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("Swift.Equatable"));

        Assert.Single(protocolDecl.InheritedProtocols);
    }

    [Fact]
    public void ProtocolDecl_WithMultipleInheritedProtocols_CollectsAll()
    {
        var protocolDecl = CreateProtocolDecl("Identifiable");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("Swift.Equatable"));
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("Swift.Hashable"));

        Assert.Equal(2, protocolDecl.InheritedProtocols.Count);
    }

    [Fact]
    public void ProtocolDecl_InheritedProtocolNames_ArePreserved()
    {
        var protocolDecl = CreateProtocolDecl("MyProtocol");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("Swift.Equatable"));
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("Foundation.NSObjectProtocol"));

        Assert.Equal("Swift.Equatable", protocolDecl.InheritedProtocols[0].Name);
        Assert.Equal("Foundation.NSObjectProtocol", protocolDecl.InheritedProtocols[1].Name);
    }

    #endregion

    #region Class Bound Tests

    [Fact]
    public void ProtocolDecl_ClassBound_IsClassBoundTrue()
    {
        var protocolDecl = CreateProtocolDecl("ClassOnlyProtocol");
        protocolDecl.IsClassBound = true;

        Assert.True(protocolDecl.IsClassBound);
    }

    [Fact]
    public void ProtocolDecl_NotClassBound_IsClassBoundFalse()
    {
        var protocolDecl = CreateProtocolDecl("AnyTypeProtocol");

        Assert.False(protocolDecl.IsClassBound);
    }

    [Fact]
    public void ProtocolDecl_WithAnyObjectInherited_IsClassBound()
    {
        var protocolDecl = CreateProtocolDecl("ObjectProtocol");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("Swift.AnyObject"));
        protocolDecl.IsClassBound = true;

        Assert.True(protocolDecl.IsClassBound);
    }

    #endregion

    #region Generic Signature Tests

    [Fact]
    public void ProtocolDecl_WithGenericSignature_HasGenericSignature()
    {
        var protocolDecl = CreateProtocolDecl("Equatable");
        protocolDecl.GenericSignature = "<Self where Self: Equatable>";

        Assert.NotNull(protocolDecl.GenericSignature);
    }

    [Fact]
    public void ProtocolDecl_GenericSignatureContainsSelf_IndicatesSelfRequirement()
    {
        var protocolDecl = CreateProtocolDecl("Comparable");
        protocolDecl.GenericSignature = "<Self where Self: Comparable>";
        protocolDecl.HasSelfRequirement = true;

        Assert.Contains("Self", protocolDecl.GenericSignature);
        Assert.True(protocolDecl.HasSelfRequirement);
    }

    #endregion

    #region Properties Tests

    [Fact]
    public void ProtocolDecl_CanHaveProperties()
    {
        var protocolDecl = CreateProtocolDecl("Identifiable");
        protocolDecl.Properties.Add(CreatePropertyDecl("id", "Swift.Int"));

        Assert.Single(protocolDecl.Properties);
    }

    [Fact]
    public void ProtocolDecl_PropertyWithGetterOnly_HasGetAccessor()
    {
        var protocolDecl = CreateProtocolDecl("ReadOnlyProtocol");
        protocolDecl.Properties.Add(CreatePropertyDecl("value", "Swift.String", hasGetter: true, hasSetter: false));

        Assert.Single(protocolDecl.Properties[0].Accessors);
    }

    [Fact]
    public void ProtocolDecl_PropertyWithGetterAndSetter_HasBothAccessors()
    {
        var protocolDecl = CreateProtocolDecl("MutableProtocol");
        protocolDecl.Properties.Add(CreatePropertyDecl("value", "Swift.String", hasGetter: true, hasSetter: true));

        Assert.Equal(2, protocolDecl.Properties[0].Accessors.Count);
    }

    [Fact]
    public void ProtocolDecl_StaticProperty_IsStaticTrue()
    {
        var protocolDecl = CreateProtocolDecl("NamedProtocol");
        protocolDecl.Properties.Add(CreatePropertyDecl("name", "Swift.String", isStatic: true));

        Assert.True(protocolDecl.Properties[0].IsStatic);
    }

    #endregion

    #region Methods Tests

    [Fact]
    public void ProtocolDecl_CanHaveMethods()
    {
        var protocolDecl = CreateProtocolDecl("Drawable");
        protocolDecl.Methods.Add(CreateMethodDecl("draw"));

        Assert.Single(protocolDecl.Methods);
    }

    [Fact]
    public void ProtocolDecl_WithMultipleMethods_CollectsAll()
    {
        var protocolDecl = CreateProtocolDecl("DataSource");
        protocolDecl.Methods.Add(CreateMethodDecl("numberOfItems"));
        protocolDecl.Methods.Add(CreateMethodDecl("itemAt"));
        protocolDecl.Methods.Add(CreateMethodDecl("configure"));

        Assert.Equal(3, protocolDecl.Methods.Count);
    }

    [Fact]
    public void ProtocolDecl_StaticMethod_HasStaticMethodType()
    {
        var protocolDecl = CreateProtocolDecl("Factory");
        protocolDecl.Methods.Add(CreateMethodDecl("create", isStatic: true));

        Assert.Equal(MethodType.Static, protocolDecl.Methods[0].MethodType);
    }

    [Fact]
    public void ProtocolDecl_ThrowingMethod_HasThrowsTrue()
    {
        var protocolDecl = CreateProtocolDecl("Fetcher");
        protocolDecl.Methods.Add(CreateMethodDecl("fetch", throws: true));

        Assert.True(protocolDecl.Methods[0].Throws);
    }

    [Fact]
    public void ProtocolDecl_AsyncMethod_HasIsAsyncTrue()
    {
        var protocolDecl = CreateProtocolDecl("AsyncFetcher");
        protocolDecl.Methods.Add(CreateMethodDecl("fetchAsync", isAsync: true));

        Assert.True(protocolDecl.Methods[0].IsAsync);
    }

    #endregion

    #region CanBeExistential Tests

    [Fact]
    public void ProtocolDecl_SimpleProtocol_CanBeExistential()
    {
        var protocolDecl = CreateProtocolDecl("SimpleProtocol");

        Assert.True(protocolDecl.CanBeExistential);
    }

    [Fact]
    public void ProtocolDecl_WithBothSelfAndAssociatedTypes_CannotBeExistential()
    {
        var protocolDecl = CreateProtocolDecl("ComplexProtocol");
        protocolDecl.HasSelfRequirement = true;
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });

        Assert.False(protocolDecl.CanBeExistential);
    }

    #endregion

    #region Unsupported Module Skip Tests

    [Fact]
    public void HasMembersReferencingUnsupportedModule_SwiftUIProperty_ReturnsTrue()
    {
        var protocolDecl = CreateProtocolDecl("ThemeProtocol");
        protocolDecl.Properties.Add(CreatePropertyDecl("primaryColor", "SwiftUI.Color"));

        Assert.True(ModuleHandler.HasMembersReferencingUnsupportedModule(protocolDecl));
    }

    [Fact]
    public void HasMembersReferencingUnsupportedModule_SupportedProperty_ReturnsFalse()
    {
        var protocolDecl = CreateProtocolDecl("CounterProtocol");
        protocolDecl.Properties.Add(CreatePropertyDecl("count", "Swift.Int"));

        Assert.False(ModuleHandler.HasMembersReferencingUnsupportedModule(protocolDecl));
    }

    [Fact]
    public void HasMembersReferencingUnsupportedModule_StaticSwiftUIProperty_ReturnsFalse()
    {
        // Static properties are skipped in the check (not part of EveryProtocol conformance)
        var protocolDecl = CreateProtocolDecl("ThemeProtocol");
        protocolDecl.Properties.Add(CreatePropertyDecl("defaultColor", "SwiftUI.Color", isStatic: true));

        Assert.False(ModuleHandler.HasMembersReferencingUnsupportedModule(protocolDecl));
    }

    [Fact]
    public void HasMembersReferencingUnsupportedModule_CombineProperty_ReturnsTrue()
    {
        var protocolDecl = CreateProtocolDecl("StreamProtocol");
        protocolDecl.Properties.Add(CreatePropertyDecl("publisher", "Combine.AnyPublisher"));

        Assert.True(ModuleHandler.HasMembersReferencingUnsupportedModule(protocolDecl));
    }

    [Fact]
    public void HasMembersReferencingUnsupportedModule_MethodWithSwiftUIArg_ReturnsTrue()
    {
        var protocolDecl = CreateProtocolDecl("Renderer");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "render",
            MangledName = "$srender",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Color"),
                    Name = "color",
                    PrivateName = "color",
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
            IsSynthesizedAccessor = false
        });

        Assert.True(ModuleHandler.HasMembersReferencingUnsupportedModule(protocolDecl));
    }

    [Fact]
    public void HasMembersReferencingUnsupportedModule_EmptyProtocol_ReturnsFalse()
    {
        var protocolDecl = CreateProtocolDecl("EmptyProtocol");

        Assert.False(ModuleHandler.HasMembersReferencingUnsupportedModule(protocolDecl));
    }

    #endregion

    #region F1: DIM Overload Tests

    [Fact]
    public void DimOverload_NintParam_EmitsIntDim()
    {
        var method = CreateMethodDeclWithNintParam("skip", "count", "Swift.Int");
        var output = EmitDimOverload(method);

        Assert.Contains("void Skip(int count) => Skip((nint)count);", output);
    }

    [Fact]
    public void DimOverload_NuintParam_EmitsUintDim()
    {
        var method = CreateMethodDeclWithNintParam("index", "position", "Swift.UInt");
        var output = EmitDimOverload(method);

        Assert.Contains("void Index(uint position) => Index((nuint)position);", output);
    }

    [Fact]
    public void DimOverload_OptionalNintParam_EmitsNullableIntDim()
    {
        var optNint = new NamedTypeSpec("Swift.Optional");
        optNint.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodDeclWithTypeSpecParam("setLimit", "limit", optNint);
        var output = EmitDimOverload(method);

        Assert.Contains("int? limit", output);
        Assert.Contains("(nint?)limit", output);
    }

    [Fact]
    public void DimOverload_OptionalNuintParam_EmitsNullableUintDim()
    {
        var optNuint = new NamedTypeSpec("Swift.Optional");
        optNuint.GenericParameters.Add(new NamedTypeSpec("Swift.UInt"));
        var method = CreateMethodDeclWithTypeSpecParam("setIndex", "position", optNuint);
        var output = EmitDimOverload(method);

        Assert.Contains("uint? position", output);
        Assert.Contains("(nuint?)position", output);
    }

    [Fact]
    public void DimOverload_NoNintParams_EmitsNothing()
    {
        var method = CreateMethodDeclWithNintParam("getName", "id", "Swift.String");
        var output = EmitDimOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void DimOverload_AsyncMethod_Skipped()
    {
        var method = CreateMethodDeclWithNintParam("fetch", "count", "Swift.Int");
        method.IsAsync = true;
        var output = EmitDimOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void DimOverload_ReturnTypeNotNarrowed()
    {
        // DIM return types stay as nint/nuint — same overload resolution safety
        var method = CreateMethodDeclWithNintParam("getCount", "offset", "Swift.Int",
            returnType: new NamedTypeSpec("Swift.Int"));
        var output = EmitDimOverload(method);

        // Return type should resolve to nint (not narrowed to int)
        Assert.Contains("GetCount(int offset) => GetCount((nint)offset);", output);
        Assert.DoesNotContain("(int)GetCount", output);
    }

    [Fact]
    public void DimOverload_DuplicateKey_SkipsSecond()
    {
        var method = CreateMethodDeclWithNintParam("process", "count", "Swift.Int");
        var typeDb = CreateDimTypeDatabase();
        var keys = new HashSet<string>();
        var handler = new ProtocolHandler(NullLogger.Instance);

        // First emission
        var writer1 = new StringWriter();
        var csWriter1 = new CSharpWriter(writer1);
        handler.TryEmitInterfaceMethodNintOverload(csWriter1, method, typeDb, null, keys);
        var firstOutput = writer1.ToString();

        // Second emission with same method
        var writer2 = new StringWriter();
        var csWriter2 = new CSharpWriter(writer2);
        handler.TryEmitInterfaceMethodNintOverload(csWriter2, method, typeDb, null, keys);
        var secondOutput = writer2.ToString();

        Assert.NotEmpty(firstOutput);
        Assert.Equal(string.Empty, secondOutput);
    }

    [Fact]
    public void DimOverload_NullableRefParamNormalized_CollidesWithNonNullable()
    {
        // C# overload identity ignores nullable annotations on reference types:
        // Method(int, string?) ≡ Method(int, string) for overload resolution.
        // DIM key must normalize nullable ref params to match normal protocol dedup.
        var typeDb = CreateDimTypeDatabase();
        var keys = new HashSet<string>();
        var handler = new ProtocolHandler(NullLogger.Instance);

        // Pre-populate with the non-nullable signature (as if EmitInterfaceMethod already emitted it)
        keys.Add("SetItem(int,string)");

        // Method with nint param + Optional<String> param → DIM key should normalize to SetItem(int,string)
        var optString = new NamedTypeSpec("Swift.Optional");
        optString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var method = CreateMethodDeclWithTwoParams("setItem",
            ("index", new NamedTypeSpec("Swift.Int")),
            ("name", optString));

        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        handler.TryEmitInterfaceMethodNintOverload(csWriter, method, typeDb, null, keys);

        // DIM should be skipped because normalized key "SetItem(int,string)" already exists
        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void DimOverload_NoAccessModifier()
    {
        // Interface members are implicitly public — no access modifier in DIM
        var method = CreateMethodDeclWithNintParam("skip", "count", "Swift.Int");
        var output = EmitDimOverload(method);

        Assert.DoesNotContain("public ", output);
        Assert.DoesNotContain("private ", output);
    }

    private string EmitDimOverload(MethodDecl method)
    {
        var typeDb = CreateDimTypeDatabase();
        var keys = new HashSet<string>();
        var handler = new ProtocolHandler(NullLogger.Instance);
        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        handler.TryEmitInterfaceMethodNintOverload(csWriter, method, typeDb, null, keys);
        return writer.ToString();
    }

    private static TypeDatabase CreateDimTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.UInt"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "UIntPtr"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.UInt"),
                MetadataAccessor = "$sSuMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "String"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        return typeDatabase;
    }

    private static MethodDecl CreateMethodDeclWithNintParam(
        string name, string paramName, string paramSwiftType,
        TypeSpec? returnType = null)
    {
        var csSignature = new List<ArgumentDecl>
        {
            new()
            {
                SwiftTypeSpec = returnType ?? TupleTypeSpec.Empty,
                Name = "",
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            },
            new()
            {
                SwiftTypeSpec = new NamedTypeSpec(paramSwiftType),
                Name = paramName,
                PrivateName = paramName,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            }
        };

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static MethodDecl CreateMethodDeclWithTypeSpecParam(
        string name, string paramName, TypeSpec paramType)
    {
        var csSignature = new List<ArgumentDecl>
        {
            new()
            {
                SwiftTypeSpec = TupleTypeSpec.Empty,
                Name = "",
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            },
            new()
            {
                SwiftTypeSpec = paramType,
                Name = paramName,
                PrivateName = paramName,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            }
        };

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static MethodDecl CreateMethodDeclWithTwoParams(
        string name,
        (string name, TypeSpec type) param1,
        (string name, TypeSpec type) param2)
    {
        var csSignature = new List<ArgumentDecl>
        {
            new()
            {
                SwiftTypeSpec = TupleTypeSpec.Empty,
                Name = "",
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            },
            new()
            {
                SwiftTypeSpec = param1.type,
                Name = param1.name,
                PrivateName = param1.name,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            },
            new()
            {
                SwiftTypeSpec = param2.type,
                Name = param2.name,
                PrivateName = param2.name,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            }
        };

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    #endregion

    #region Helper Methods

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

    private static PropertyDecl CreatePropertyDecl(
        string name,
        string typeName,
        bool isStatic = false,
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
                    CSSignature = new List<ArgumentDecl>(),
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null,
                    Throws = false,
                    IsAsync = false,
                    IsSynthesizedAccessor = true
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
                    CSSignature = new List<ArgumentDecl>(),
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null,
                    Throws = false,
                    IsAsync = false,
                    IsSynthesizedAccessor = true
                }
            });
        }

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(typeName),
            IsStatic = isStatic,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static MethodDecl CreateMethodDecl(
        string name,
        bool isStatic = false,
        bool throws = false,
        bool isAsync = false)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
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
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = throws,
            IsAsync = isAsync,
            IsSynthesizedAccessor = false
        };
    }

    #endregion
}
