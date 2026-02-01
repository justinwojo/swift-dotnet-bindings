// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
    }

    #endregion
}
