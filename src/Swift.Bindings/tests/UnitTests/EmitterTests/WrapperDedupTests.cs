// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests that overloaded methods, properties, and constructors get unique @_cdecl wrapper symbols.
/// Uses the real symbol-name builders (GetMethodSymbolName, GetAccessorSymbolName, GetConstructorSymbolName)
/// and ModuleEmissionContext dedup APIs to verify end-to-end uniqueness.
/// </summary>
public class WrapperDedupTests
{
    [Fact]
    public void OverloadedMethods_DifferentParamCounts_GetUniqueSymbols()
    {
        // Two methods named "process" with different mangled names → unique @_cdecl symbols
        var (moduleDecl, typeDb) = CreateTestEnvironment("Processor");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Processor", moduleDecl);

        var method0 = CreateMethod("process", parentDecl, moduleDecl, paramCount: 0);
        var method1 = CreateMethod("process", parentDecl, moduleDecl, paramCount: 1);

        // Both methods should be wrappable
        var env0 = new MethodEnvironment(method0, typeDb);
        var env1 = new MethodEnvironment(method1, typeDb);
        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env0), "First process overload should be wrappable");
        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env1), "Second process overload should be wrappable");

        // Use real symbol builder — symbols differ because mangled names differ
        var symbol0 = MethodWrapperEmitter.GetMethodSymbolName("TestModule", "Processor", "process", method0.MangledName);
        var symbol1 = MethodWrapperEmitter.GetMethodSymbolName("TestModule", "Processor", "process", method1.MangledName);

        Assert.NotEqual(symbol0, symbol1);

        // Verify dedup accepts both
        var ctx = new ModuleEmissionContext();
        Assert.True(ctx.TryAddMethodWrapperSymbol(symbol0), "First overload symbol should be accepted");
        Assert.True(ctx.TryAddMethodWrapperSymbol(symbol1), "Second overload symbol should be accepted");
    }

    [Fact]
    public void PropertyGetterAndSetter_GetUniqueSymbols()
    {
        // A property with both getter and setter should produce unique symbols via Get/Set prefix
        var (moduleDecl, typeDb) = CreateTestEnvironment("Config");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Config", moduleDecl);

        var getterMethod = CreateAccessorMethod("getter:name", isGetter: true, parentDecl, moduleDecl);
        var setterMethod = CreateAccessorMethod("setter:name", isGetter: false, parentDecl, moduleDecl);

        var propertyDecl = new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod },
                new SetAccessorDecl { Method = setterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var getterEnv = new MethodEnvironment(getterMethod, typeDb);
        var setterEnv = new MethodEnvironment(setterMethod, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, getterEnv),
            "Property getter should be wrappable");
        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, setterEnv),
            "Property setter should be wrappable");

        // Use real symbol builder — symbols differ via Get_ vs Set_ prefix
        var getterSymbol = PropertyWrapperEmitter.GetAccessorSymbolName("TestModule", "Config", "name", isGetter: true);
        var setterSymbol = PropertyWrapperEmitter.GetAccessorSymbolName("TestModule", "Config", "name", isGetter: false);

        Assert.NotEqual(getterSymbol, setterSymbol);

        // Verify dedup accepts both
        var ctx = new ModuleEmissionContext();
        Assert.True(ctx.TryAddPropertyWrapperSymbol(getterSymbol), "Getter symbol should be accepted");
        Assert.True(ctx.TryAddPropertyWrapperSymbol(setterSymbol), "Setter symbol should be accepted");
    }

    [Fact]
    public void ConstructorOverloads_GetUniqueSymbols()
    {
        // Constructor overloads with different mangled names → unique @_cdecl symbols
        var (moduleDecl, typeDb) = CreateTestEnvironment("Widget");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Widget", moduleDecl);

        var ctor0 = CreateConstructor("init", parentDecl, moduleDecl, paramCount: 0);
        var ctor1 = CreateConstructor("init", parentDecl, moduleDecl, paramCount: 1);

        var env0 = new MethodEnvironment(ctor0, typeDb);
        var env1 = new MethodEnvironment(ctor1, typeDb);

        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env0),
            "Default constructor should be wrappable");
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(env1),
            "Parameterized constructor should be wrappable");

        // Use real symbol builder — symbols differ because mangled names differ
        var symbol0 = ConstructorWrapperEmitter.GetConstructorSymbolName("TestModule", "Widget", ctor0.MangledName);
        var symbol1 = ConstructorWrapperEmitter.GetConstructorSymbolName("TestModule", "Widget", ctor1.MangledName);

        Assert.NotEqual(symbol0, symbol1);

        // Verify dedup accepts both
        var ctx = new ModuleEmissionContext();
        Assert.True(ctx.TryAddConstructorWrapperSymbol(symbol0), "Default constructor symbol should be accepted");
        Assert.True(ctx.TryAddConstructorWrapperSymbol(symbol1), "Parameterized constructor symbol should be accepted");
    }

    [Fact]
    public void EmissionContext_DeduplicatesWrapperSymbols()
    {
        // ModuleEmissionContext should reject duplicate symbols
        var ctx = new ModuleEmissionContext();

        var methodSymbol = MethodWrapperEmitter.GetMethodSymbolName("TestModule", "Foo", "bar", "$s_bar_0");
        Assert.True(ctx.TryAddMethodWrapperSymbol(methodSymbol));
        Assert.False(ctx.TryAddMethodWrapperSymbol(methodSymbol), "Duplicate method symbol should be rejected");

        var propertySymbol = PropertyWrapperEmitter.GetAccessorSymbolName("TestModule", "Foo", "baz", isGetter: true);
        Assert.True(ctx.TryAddPropertyWrapperSymbol(propertySymbol));
        Assert.False(ctx.TryAddPropertyWrapperSymbol(propertySymbol), "Duplicate property symbol should be rejected");

        var ctorSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName("TestModule", "Foo", "$s_init_0");
        Assert.True(ctx.TryAddConstructorWrapperSymbol(ctorSymbol));
        Assert.False(ctx.TryAddConstructorWrapperSymbol(ctorSymbol), "Duplicate constructor symbol should be rejected");
    }

    [Fact]
    public void EmissionContext_DifferentWrapperTypes_IndependentNamespaces()
    {
        // Symbols for different wrapper types (method vs property vs constructor) are tracked separately
        var ctx = new ModuleEmissionContext();

        // Use the same base name but different symbol builders → should all succeed
        var methodSymbol = MethodWrapperEmitter.GetMethodSymbolName("TestModule", "Foo", "bar", "$s_bar_0");
        var propertySymbol = PropertyWrapperEmitter.GetAccessorSymbolName("TestModule", "Foo", "bar", isGetter: true);
        var ctorSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName("TestModule", "Foo", "$s_bar_0");

        Assert.True(ctx.TryAddMethodWrapperSymbol(methodSymbol));
        Assert.True(ctx.TryAddPropertyWrapperSymbol(propertySymbol));
        Assert.True(ctx.TryAddConstructorWrapperSymbol(ctorSymbol));

        // But duplicates within same type are still rejected
        Assert.False(ctx.TryAddMethodWrapperSymbol(methodSymbol));
        Assert.False(ctx.TryAddPropertyWrapperSymbol(propertySymbol));
        Assert.False(ctx.TryAddConstructorWrapperSymbol(ctorSymbol));
    }

    [Fact]
    public void IdenticalMangledNames_ProduceSameSymbol_RejectedByDedup()
    {
        // If two methods have identical mangled names (shouldn't happen, but defensive),
        // the symbol builder returns the same string and dedup catches it
        var symbol1 = MethodWrapperEmitter.GetMethodSymbolName("TestModule", "Foo", "bar", "$s_SAME");
        var symbol2 = MethodWrapperEmitter.GetMethodSymbolName("TestModule", "Foo", "bar", "$s_SAME");
        Assert.Equal(symbol1, symbol2);

        var ctx = new ModuleEmissionContext();
        Assert.True(ctx.TryAddMethodWrapperSymbol(symbol1));
        Assert.False(ctx.TryAddMethodWrapperSymbol(symbol2), "Identical symbols from same mangled name should be caught");
    }

    #region Test Helpers

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment(string typeName)
    {
        var typeDb = new TypeDatabase();

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
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", typeName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
                MetadataAccessor = $"$s10TestModule{typeName.Length}{typeName}VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(testModule);

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

        return (moduleDecl, typeDb);
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
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
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static MethodDecl CreateMethod(string name, TypeDecl parentDecl, ModuleDecl moduleDecl, int paramCount = 0)
    {
        var csSignature = new List<ArgumentDecl>
        {
            // Return type (void)
            new ArgumentDecl
            {
                SwiftTypeSpec = TupleTypeSpec.Empty,
                Name = "",
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl
            }
        };

        for (int i = 0; i < paramCount; i++)
        {
            csSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = $"arg{i}",
                PrivateName = $"arg{i}",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl
            });
        }

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}_{paramCount}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static MethodDecl CreateConstructor(string name, TypeDecl parentDecl, ModuleDecl moduleDecl, int paramCount = 0)
    {
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = TupleTypeSpec.Empty,
                Name = "",
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl
            }
        };

        for (int i = 0; i < paramCount; i++)
        {
            csSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = $"arg{i}",
                PrivateName = $"arg{i}",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl
            });
        }

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_init_{paramCount}",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static MethodDecl CreateAccessorMethod(string name, bool isGetter, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_accessor_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    #endregion
}
