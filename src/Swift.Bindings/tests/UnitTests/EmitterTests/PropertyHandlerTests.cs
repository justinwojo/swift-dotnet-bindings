// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for property accessors (getters and setters).
/// </summary>
public class PropertyHandlerTests
{
    #region AccessorDecl Tests

    [Fact]
    public void GetAccessorDecl_CanBeCreated()
    {
        var methodDecl = CreateMethodDecl("TestProperty_Get", MethodType.Instance);
        var accessor = new GetAccessorDecl { Method = methodDecl };

        Assert.NotNull(accessor);
        Assert.NotNull(accessor.Method);
        Assert.Equal("TestProperty_Get", accessor.Method.Name);
    }

    [Fact]
    public void SetAccessorDecl_CanBeCreated()
    {
        var methodDecl = CreateMethodDecl("TestProperty_Set", MethodType.Instance);
        var accessor = new SetAccessorDecl { Method = methodDecl };

        Assert.NotNull(accessor);
        Assert.NotNull(accessor.Method);
        Assert.Equal("TestProperty_Set", accessor.Method.Name);
    }

    [Fact]
    public void AccessorDecl_GetterAndSetter_AreDifferentTypes()
    {
        var getterMethod = CreateMethodDecl("TestProperty_Get", MethodType.Instance);
        var setterMethod = CreateMethodDecl("TestProperty_Set", MethodType.Instance);

        AccessorDecl getter = new GetAccessorDecl { Method = getterMethod };
        AccessorDecl setter = new SetAccessorDecl { Method = setterMethod };

        Assert.IsType<GetAccessorDecl>(getter);
        Assert.IsType<SetAccessorDecl>(setter);
        Assert.NotEqual(getter.GetType(), setter.GetType());
    }

    #endregion

    #region PropertyDecl Accessor Tests

    [Fact]
    public void PropertyDecl_WithOnlyGetter_IsReadOnly()
    {
        var property = CreatePropertyDecl("ReadOnlyProp", hasGetter: true, hasSetter: false);

        Assert.Single(property.Accessors);
        Assert.Single(property.Accessors.OfType<GetAccessorDecl>());
        Assert.Empty(property.Accessors.OfType<SetAccessorDecl>());
    }

    [Fact]
    public void PropertyDecl_WithGetterAndSetter_IsReadWrite()
    {
        var property = CreatePropertyDecl("ReadWriteProp", hasGetter: true, hasSetter: true);

        Assert.Equal(2, property.Accessors.Count);
        Assert.Single(property.Accessors.OfType<GetAccessorDecl>());
        Assert.Single(property.Accessors.OfType<SetAccessorDecl>());
    }

    [Fact]
    public void PropertyDecl_CanFilterAccessorsByType()
    {
        var property = CreatePropertyDecl("MixedProp", hasGetter: true, hasSetter: true);

        var getter = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        var setter = property.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();

        Assert.NotNull(getter);
        Assert.NotNull(setter);
        Assert.Contains("_Get", getter!.Method.Name);
        Assert.Contains("_Set", setter!.Method.Name);
    }

    [Fact]
    public void PropertyDecl_StaticProperty_HasStaticAccessors()
    {
        var property = CreatePropertyDecl("StaticProp", hasGetter: true, hasSetter: true, isStatic: true);

        var getter = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        var setter = property.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();

        Assert.NotNull(getter);
        Assert.NotNull(setter);
        Assert.Equal(MethodType.Static, getter!.Method.MethodType);
        Assert.Equal(MethodType.Static, setter!.Method.MethodType);
    }

    [Fact]
    public void PropertyDecl_InstanceProperty_HasInstanceAccessors()
    {
        var property = CreatePropertyDecl("InstanceProp", hasGetter: true, hasSetter: true, isStatic: false);

        var getter = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        var setter = property.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();

        Assert.NotNull(getter);
        Assert.NotNull(setter);
        Assert.Equal(MethodType.Instance, getter!.Method.MethodType);
        Assert.Equal(MethodType.Instance, setter!.Method.MethodType);
    }

    #endregion

    #region Setter Method Signature Tests

    [Fact]
    public void SetterMethod_HasVoidReturnType()
    {
        var property = CreatePropertyDecl("TestProp", hasGetter: false, hasSetter: true);
        var setter = property.Accessors.OfType<SetAccessorDecl>().First();

        // First element in CSSignature is the return type
        var returnType = setter.Method.CSSignature[0];
        Assert.IsType<TupleTypeSpec>(returnType.SwiftTypeSpec);
        Assert.True(((TupleTypeSpec)returnType.SwiftTypeSpec).IsEmptyTuple);
    }

    [Fact]
    public void SetterMethod_HasValueParameter()
    {
        var property = CreatePropertyDecl("TestProp", hasGetter: false, hasSetter: true);
        var setter = property.Accessors.OfType<SetAccessorDecl>().First();

        // Second element in CSSignature is the value parameter
        Assert.Equal(2, setter.Method.CSSignature.Count);
        var valueParam = setter.Method.CSSignature[1];
        Assert.Equal("value", valueParam.Name);
    }

    [Fact]
    public void SetterMethod_HasCorrectPropertyType()
    {
        var property = CreatePropertyDecl("TestProp", hasGetter: false, hasSetter: true, propertyType: "Swift.Int");
        var setter = property.Accessors.OfType<SetAccessorDecl>().First();

        var valueParam = setter.Method.CSSignature[1];
        Assert.IsType<NamedTypeSpec>(valueParam.SwiftTypeSpec);
        // NameWithoutModule returns just the type name, not the full qualified name
        Assert.Equal("Int", ((NamedTypeSpec)valueParam.SwiftTypeSpec).NameWithoutModule);
    }

    #endregion

    #region Property Emission Tests

    [Fact]
    public void Emit_WithGetterAndSetter_EmitsAccessorMethodsAndProperty()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Counter", moduleDecl);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "count", "Swift.Int", hasGetter: true, hasSetter: true);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        Assert.Contains("public System.Int64 Count", csOutput);
        Assert.Contains("get => Count_Get();", csOutput);
        Assert.Contains("set => Count_Set(value);", csOutput);
        Assert.Contains("public unsafe System.Int64 Count_Get()", csOutput);
        Assert.Contains("public unsafe void Count_Set(", csOutput);
    }

    [Fact]
    public void Emit_WithNoAccessors_EmitsNothing()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Counter", moduleDecl);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "count", "Swift.Int", hasGetter: false, hasSetter: false);

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_WhenPropertyNameMatchesContainingType_AppendsValueSuffix()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Animation", moduleDecl);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "animation", "Swift.Int", hasGetter: true, hasSetter: false);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        Assert.Contains("public System.Int64 AnimationValue", csOutput);
        Assert.DoesNotContain("public System.Int64 Animation\n", csOutput);
    }

    [Fact]
    public void Emit_AsyncStreamProperty_EmitsAsyncEnumerableAndSwiftWrapper()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Feed", moduleDecl);
        var property = new PropertyDecl
        {
            Name = "updates",
            SwiftTypeSpec = new NamedTypeSpec("_Concurrency.AsyncStream", new NamedTypeSpec("Swift.Int")),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        Assert.Contains("public IAsyncEnumerable<System.Int64> Updates", csOutput);
        Assert.Contains("private static unsafe byte updates_AsyncStream_OnElement", csOutput);
        Assert.Contains("PInvoke_Feed_updates_AsyncStream", csOutput);
        Assert.Contains("public func Feed_updates_AsyncStream", swiftOutput);
        Assert.Contains("for await element in self.updates", swiftOutput);
    }

    [Fact]
    public void Emit_PropertyWithUnsupportedClosureFallback_EmitsUnsupportedSwiftTypeAttribute()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Loader", moduleDecl);
        var unsupportedClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new NamedTypeSpec("T")),
            TupleTypeSpec.Empty);
        var propertyType = new NamedTypeSpec("TestModule.Box", unsupportedClosure);
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "handler", propertyType, hasGetter: true, hasSetter: false);

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        Assert.Contains("[global::Swift.UnsupportedSwiftType(\"Unsupported closure fallback\",", csOutput);
        Assert.Contains("public Swift.TestModule.Box<object> Handler", csOutput);
    }

    [Fact]
    public void Emit_PropertyWithExistentialBoundGeneric_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Loader", moduleDecl);
        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        var propertyType = new NamedTypeSpec("TestModule.Box", existentialArg);
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "cache", propertyType, hasGetter: true, hasSetter: false);

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_PropertyWithUnsatisfiedBoundGenericConstraint_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Loader", moduleDecl);

        var constrainedStorageDecl = CreateGenericStructDecl(
            "ValueProviderStorage",
            moduleDecl,
            "T",
            "TestModule.AnyInterpolatable");
        CreateStructDeclForEmission("LottieVector3D", moduleDecl);

        var propertyType = new NamedTypeSpec(
            constrainedStorageDecl.SwiftTypeName.ModuleQualifiedName,
            new NamedTypeSpec("TestModule.LottieVector3D"));
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "storage", propertyType, hasGetter: true, hasSetter: false);

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_OptionalExistentialProperty_WithUnsatisfiedAccessorConstraint_SkipsPropertyWrapper()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Loader", moduleDecl);

        CreateGenericStructDecl(
            "ValueProviderStorage",
            moduleDecl,
            "T",
            "TestModule.AnyInterpolatable");

        var optionalExistentialType = new NamedTypeSpec(
            "Swift.Optional",
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.DataCaching") }));
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "dataCache", optionalExistentialType, hasGetter: true, hasSetter: true);

        // Simulate accessor signatures that use an unsatisfied constrained bound generic.
        var unsatisfiedAccessorType = new NamedTypeSpec(
            "TestModule.ValueProviderStorage",
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Double")));
        foreach (var accessor in property.Accessors)
        {
            if (accessor is GetAccessorDecl)
            {
                accessor.Method.CSSignature[0].SwiftTypeSpec = unsatisfiedAccessorType;
            }
            else if (accessor is SetAccessorDecl)
            {
                accessor.Method.CSSignature[1].SwiftTypeSpec = unsatisfiedAccessorType;
            }
        }

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_PropertyWithUnsatisfiedGetterReturnConstraint_SkipsPropertyWrapper()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Loader", moduleDecl);

        CreateGenericStructDecl(
            "ValueProviderStorage",
            moduleDecl,
            "T",
            "TestModule.AnyInterpolatable");

        var propertyType = new NamedTypeSpec("TestModule.Payload");
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "dataCache", propertyType, hasGetter: true, hasSetter: false);

        var getter = property.Accessors.OfType<GetAccessorDecl>().Single();
        getter.Method.CSSignature[0].SwiftTypeSpec = new NamedTypeSpec(
            "TestModule.ValueProviderStorage",
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Double")));

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_PropertyWithSwiftStringAccessor_EmitsWithStringConversionBridge()
    {
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Loader", moduleDecl);
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "name", "Swift.String", hasGetter: true, hasSetter: true);

        var (csOutput, swiftOutput) = EmitProperty(property, typeDatabase);

        // Property type should be idiomatic string
        Assert.Contains("public string Name", csOutput);
        // Getter should bridge via .ToString()
        Assert.Contains("get => Name_Get().ToString();", csOutput);
        // Setter should bridge via new SwiftString(value)
        Assert.Contains("set => Name_Set(new SwiftString(value));", csOutput);
    }

    #endregion

    #region Helper Methods

    private static MethodDecl CreateMethodDecl(string name, MethodType methodType)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = methodType,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = string.Empty,
                    PrivateName = string.Empty,
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
        };
    }

    private static PropertyDecl CreatePropertyDecl(
        string name,
        bool hasGetter,
        bool hasSetter,
        bool isStatic = false,
        string propertyType = "Swift.Int")
    {
        var accessors = new List<AccessorDecl>();
        var methodType = isStatic ? MethodType.Static : MethodType.Instance;

        if (hasGetter)
        {
            var getterMethod = new MethodDecl
            {
                Name = $"{name}_Get",
                MangledName = $"$s{name}g",
                MethodType = methodType,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>
                {
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = new NamedTypeSpec(propertyType),
                        Name = string.Empty,
                        PrivateName = string.Empty,
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
            };
            accessors.Add(new GetAccessorDecl { Method = getterMethod });
        }

        if (hasSetter)
        {
            var setterMethod = new MethodDecl
            {
                Name = $"{name}_Set",
                MangledName = $"$s{name}s",
                MethodType = methodType,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>
                {
                    // Return type (void)
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = TupleTypeSpec.Empty,
                        Name = string.Empty,
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = false,
                        ParentDecl = null,
                        ModuleDecl = null
                    },
                    // Value parameter
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = new NamedTypeSpec(propertyType),
                        Name = "value",
                        PrivateName = string.Empty,
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
            };
            accessors.Add(new SetAccessorDecl { Method = setterMethod });
        }

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(propertyType),
            IsStatic = isStatic,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static PropertyDecl CreatePropertyDeclWithTypeSpec(
        string name,
        TypeSpec typeSpec,
        bool hasGetter,
        bool hasSetter,
        bool isStatic = false)
    {
        var accessors = new List<AccessorDecl>();
        var methodType = isStatic ? MethodType.Static : MethodType.Instance;

        if (hasGetter)
        {
            var getterMethod = new MethodDecl
            {
                Name = $"{name}_Get",
                MangledName = $"$s{name}g",
                MethodType = methodType,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>
                {
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = typeSpec,
                        Name = string.Empty,
                        PrivateName = string.Empty,
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
            };
            accessors.Add(new GetAccessorDecl { Method = getterMethod });
        }

        if (hasSetter)
        {
            var setterMethod = new MethodDecl
            {
                Name = $"{name}_Set",
                MangledName = $"$s{name}s",
                MethodType = methodType,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>
                {
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = TupleTypeSpec.Empty,
                        Name = string.Empty,
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = false,
                        ParentDecl = null,
                        ModuleDecl = null
                    },
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = typeSpec,
                        Name = "value",
                        PrivateName = string.Empty,
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
            };
            accessors.Add(new SetAccessorDecl { Method = setterMethod });
        }

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = typeSpec,
            IsStatic = isStatic,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static TypeDatabase CreateTypeDatabaseWithInt()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Box"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
                MetadataAccessor = "$s10TestModule3BoxVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDeclForEmission(string moduleName)
    {
        return new ModuleDecl
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
    }

    private static ClassDecl CreateClassDeclForEmission(string className, ModuleDecl moduleDecl)
    {
        var classDecl = new ClassDecl
        {
            Name = className,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{className}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{className.Length}{className}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static StructDecl CreateStructDeclForEmission(string structName, ModuleDecl moduleDecl)
    {
        var structDecl = new StructDecl
        {
            Name = structName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{structName}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{structName.Length}{structName}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{structName.Length}{structName}VMa"
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static StructDecl CreateGenericStructDecl(string structName, ModuleDecl moduleDecl, string typeParameterName, string constraintProtocolName)
    {
        var structDecl = CreateStructDeclForEmission(structName, moduleDecl);
        structDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new(
                TypeName: "τ_0_0",
                SugaredTypeName: typeParameterName,
                GenericConformances: new List<GenericParameterConformance>
                {
                    new(
                        Path: new[] { "τ_0_0" },
                        ConformanceTarget: SwiftTypeName.FromModuleQualifiedName(constraintProtocolName),
                        Kind: ConformanceKind.Protocol)
                },
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };
        return structDecl;
    }

    private static PropertyDecl CreateEmittablePropertyDecl(
        ClassDecl classDecl,
        ModuleDecl moduleDecl,
        string name,
        string propertyType,
        bool hasGetter,
        bool hasSetter)
    {
        var accessors = new List<AccessorDecl>();
        var property = new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(propertyType),
            IsStatic = false,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        };

        if (hasGetter)
        {
            accessors.Add(new GetAccessorDecl
            {
                Method = new MethodDecl
                {
                    Name = $"{name}_Get",
                    MangledName = $"$s{name}g",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = new NamedTypeSpec(propertyType),
                            Name = string.Empty,
                            PrivateName = string.Empty,
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
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = TupleTypeSpec.Empty,
                            Name = string.Empty,
                            PrivateName = string.Empty,
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl
                        },
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = new NamedTypeSpec(propertyType),
                            Name = "value",
                            PrivateName = string.Empty,
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
                }
            });
        }

        classDecl.Properties.Add(property);
        return property;
    }

    private static PropertyDecl CreateEmittablePropertyDeclWithTypeSpec(
        ClassDecl classDecl,
        ModuleDecl moduleDecl,
        string name,
        TypeSpec propertyType,
        bool hasGetter,
        bool hasSetter)
    {
        var accessors = new List<AccessorDecl>();
        var property = new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = propertyType,
            IsStatic = false,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        };

        if (hasGetter)
        {
            accessors.Add(new GetAccessorDecl
            {
                Method = new MethodDecl
                {
                    Name = $"{name}_Get",
                    MangledName = $"$s{name}g",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = propertyType,
                            Name = string.Empty,
                            PrivateName = string.Empty,
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
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = TupleTypeSpec.Empty,
                            Name = string.Empty,
                            PrivateName = string.Empty,
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl
                        },
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = propertyType,
                            Name = "value",
                            PrivateName = string.Empty,
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
                }
            });
        }

        classDecl.Properties.Add(property);
        return property;
    }

    private static (string csOutput, string swiftOutput) EmitProperty(PropertyDecl property, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new PropertyHandler(new NullLogger<PropertyHandler>());
        var env = handler.Marshal(property, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static void RegisterProtocol(TypeDatabase typeDatabase, string protocolName)
    {
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName(protocolName), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", protocolName.Split('.')[1]),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                MetadataAccessor = "$s10TestModule8ProtocolPAAWP",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            })
        });
    }

    #endregion

    #region G2 — Optional Existential Property Pass-Through Tests

    [Fact]
    public void Emit_OptionalExistentialProperty_EmitsSimplePassThrough()
    {
        // G2 fix: Optional-existential properties emit simple pass-through
        // instead of going through TypeConversionHandler (which would apply
        // SwiftOptional conversion, producing incorrect ToNullable() calls).
        var typeDatabase = CreateTypeDatabaseWithInt();
        RegisterProtocol(typeDatabase, "TestModule.DataCaching");

        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Processor", moduleDecl);

        var optionalExistentialType = new NamedTypeSpec(
            "Swift.Optional",
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.DataCaching") }));
        // Accessor CSSignature uses a registered type (Swift.Int) because the unit test
        // TypeDatabase doesn't have Swift.Optional registered — using the raw optional-existential
        // TypeSpec would cause the accessor pre-flight (SignatureHandler.GetWrapperSignature()
        // → ContainsPlaceholder) to skip the property before reaching EmitGetter/EmitSetter.
        // In production, the parser resolves accessor CSSignature types separately from the
        // property's SwiftTypeSpec. This test isolates the G2 fix: when isOptionalExistential=true,
        // EmitGetter/EmitSetter emit simple pass-through delegation.
        var registeredType = new NamedTypeSpec("Swift.Int");
        var property = CreateEmittablePropertyDeclWithTypeSpec(classDecl, moduleDecl, "dataCache", registeredType, hasGetter: true, hasSetter: true);
        property.SwiftTypeSpec = optionalExistentialType;

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // Existential pass-through: simple delegation without type conversion
        Assert.Contains("get => DataCache_Get();", csOutput);
        Assert.Contains("set => DataCache_Set(value);", csOutput);
        // Should NOT apply SwiftOptional conversion (.ToNullable / new SwiftOptional)
        Assert.DoesNotContain("ToNullable", csOutput);
    }

    #endregion

    #region G1 — Generic Type Params in Properties Tests

    [Fact]
    public void Emit_PropertyWithGenericTypeParameter_ResolvesToT0NotAnyType()
    {
        // G1 fix: Properties on generic types where the property type is τ_0_0
        // resolve to "T0" via GenericContext, not "AnyType".
        var typeDatabase = CreateTypeDatabaseWithInt();
        var moduleDecl = CreateModuleDeclForEmission("TestModule");
        var classDecl = CreateClassDeclForEmission("Container", moduleDecl);
        classDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new(
                TypeName: "τ_0_0",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };

        // Create property with a registered type initially, then override to generic param
        var property = CreateEmittablePropertyDecl(classDecl, moduleDecl, "value", "Swift.Int", hasGetter: true, hasSetter: false);
        property.SwiftTypeSpec = new NamedTypeSpec("τ_0_0");

        var (csOutput, _) = EmitProperty(property, typeDatabase);

        // Generic type parameter τ_0_0 should resolve to T0
        Assert.Contains("public T0 Value", csOutput);
        Assert.DoesNotContain("AnyType", csOutput);
    }

    #endregion

    #region Existential Property Tests

    [Fact]
    public void PropertyDecl_WithSingleProtocolExistential_CanBeCreated()
    {
        // "any Equatable" is represented as NamedTypeSpec with IsAny=true
        var existentialType = new NamedTypeSpec("Swift.Equatable") { IsAny = true };
        var property = CreatePropertyDeclWithTypeSpec("delegate", existentialType, hasGetter: true, hasSetter: false);

        Assert.NotNull(property);
        Assert.Equal("delegate", property.Name);
        Assert.IsType<NamedTypeSpec>(property.SwiftTypeSpec);
        Assert.True(((NamedTypeSpec)property.SwiftTypeSpec).IsAny);
    }

    [Fact]
    public void PropertyDecl_WithProtocolComposition_CanBeCreated()
    {
        // "any P1 & P2" is represented as ProtocolListTypeSpec
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Equatable"),
            new NamedTypeSpec("Swift.Hashable")
        });
        var property = CreatePropertyDeclWithTypeSpec("constraint", protocolList, hasGetter: true, hasSetter: false);

        Assert.NotNull(property);
        Assert.IsType<ProtocolListTypeSpec>(property.SwiftTypeSpec);
    }

    [Fact]
    public void ExistentialHandler_IsSupportedExistential_SingleProtocol_ReturnsTrue()
    {
        var typeDatabase = new MockPropertyTypeDatabase();
        var handler = new ExistentialHandler(typeDatabase);

        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        Assert.True(handler.IsSupportedExistential(protocolList));
    }

    [Fact]
    public void ExistentialHandler_IsSupportedExistential_EightProtocols_ReturnsTrue()
    {
        var typeDatabase = new MockPropertyTypeDatabase();
        var handler = new ExistentialHandler(typeDatabase);

        var protocols = Enumerable.Range(1, 8)
            .Select(i => new NamedTypeSpec($"Protocol{i}"))
            .ToArray();
        var protocolList = new ProtocolListTypeSpec(protocols);

        Assert.True(handler.IsSupportedExistential(protocolList));
    }

    [Fact]
    public void ExistentialHandler_IsSupportedExistential_NineProtocols_ReturnsFalse()
    {
        var typeDatabase = new MockPropertyTypeDatabase();
        var handler = new ExistentialHandler(typeDatabase);

        var protocols = Enumerable.Range(1, 9)
            .Select(i => new NamedTypeSpec($"Protocol{i}"))
            .ToArray();
        var protocolList = new ProtocolListTypeSpec(protocols);

        Assert.False(handler.IsSupportedExistential(protocolList));
    }

    [Fact]
    public void ExistentialHandler_GetCSharpExistentialType_SingleProtocol_ReturnsContainer1()
    {
        var typeDatabase = new MockPropertyTypeDatabase();
        var handler = new ExistentialHandler(typeDatabase);

        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        var result = handler.GetCSharpExistentialType(protocolList);

        Assert.Equal("Swift.Runtime.ExistentialContainer1", result);
    }

    [Fact]
    public void ExistentialHandler_GetCSharpExistentialType_TwoProtocols_ReturnsContainer2()
    {
        var typeDatabase = new MockPropertyTypeDatabase();
        var handler = new ExistentialHandler(typeDatabase);

        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Swift.Equatable"),
            new NamedTypeSpec("Swift.Hashable")
        });
        var result = handler.GetCSharpExistentialType(protocolList);

        Assert.Equal("Swift.Runtime.ExistentialContainer2", result);
    }

    private class MockPropertyTypeDatabase : ITypeDatabase
    {
        public string AsyncLibraryName => null!;

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            record = null!;
            return false;
        }
        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
