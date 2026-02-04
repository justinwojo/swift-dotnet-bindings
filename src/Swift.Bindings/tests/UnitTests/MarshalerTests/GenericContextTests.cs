// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for GenericContext: factory methods, resolution, and merged contexts.
/// </summary>
public class GenericContextTests
{
    #region Empty Context

    [Fact]
    public void Empty_HasNoMappings()
    {
        var ctx = GenericContext.Empty;
        Assert.True(ctx.IsEmpty);
        Assert.Empty(ctx.Mapping);
    }

    [Fact]
    public void Empty_TryResolve_ReturnsFalse()
    {
        var ctx = GenericContext.Empty;
        Assert.False(ctx.TryResolve("τ_0_0", out var csName));
        Assert.Equal("", csName);
    }

    #endregion

    #region FromMethod

    [Fact]
    public void FromMethod_SingleGenericParam_MapsToT0()
    {
        var method = CreateMethodDecl("test", new[] { "τ_0_0" });
        var ctx = GenericContext.FromMethod(method);

        Assert.False(ctx.IsEmpty);
        Assert.True(ctx.TryResolve("τ_0_0", out var csName));
        Assert.Equal("T0", csName);
    }

    [Fact]
    public void FromMethod_MultipleGenericParams_MapsSequentially()
    {
        var method = CreateMethodDecl("test", new[] { "τ_0_0", "τ_0_1" });
        var ctx = GenericContext.FromMethod(method);

        Assert.True(ctx.TryResolve("τ_0_0", out var t0));
        Assert.Equal("T0", t0);
        Assert.True(ctx.TryResolve("τ_0_1", out var t1));
        Assert.Equal("T1", t1);
    }

    #endregion

    #region FromType

    [Fact]
    public void FromType_SingleGenericParam_MapsToT0()
    {
        var type = CreateGenericTypeDecl("Wrapper", new[] { "τ_0_0" });
        var ctx = GenericContext.FromType(type);

        Assert.True(ctx.TryResolve("τ_0_0", out var csName));
        Assert.Equal("T0", csName);
    }

    [Fact]
    public void FromType_TwoGenericParams_MapsSequentially()
    {
        var type = CreateGenericTypeDecl("Pair", new[] { "τ_0_0", "τ_0_1" });
        var ctx = GenericContext.FromType(type);

        Assert.True(ctx.TryResolve("τ_0_0", out var t0));
        Assert.Equal("T0", t0);
        Assert.True(ctx.TryResolve("τ_0_1", out var t1));
        Assert.Equal("T1", t1);
    }

    #endregion

    #region FromMethodInType

    [Fact]
    public void FromMethodInType_MethodWithOwnParams_OffsetsCorrectly()
    {
        // Type has τ_0_0; method has τ_1_0 (method-level param)
        var type = CreateGenericTypeDecl("Wrapper", new[] { "τ_0_0" });
        var method = CreateMethodDecl("transform", new[] { "τ_0_0", "τ_1_0" });

        var ctx = GenericContext.FromMethodInType(method, type);

        // Type param τ_0_0 → T0
        Assert.True(ctx.TryResolve("τ_0_0", out var t0));
        Assert.Equal("T0", t0);
        // Method param τ_1_0 → T1 (offset by 1)
        Assert.True(ctx.TryResolve("τ_1_0", out var t1));
        Assert.Equal("T1", t1);
    }

    [Fact]
    public void FromMethodInType_DuplicateParams_TypeTakesPrecedence()
    {
        // Parser copies type params to accessor methods, so both have τ_0_0.
        // The type-level mapping should take precedence (T0, not T1).
        var type = CreateGenericTypeDecl("Wrapper", new[] { "τ_0_0" });
        var method = CreateMethodDecl("get_wrapped", new[] { "τ_0_0" });

        var ctx = GenericContext.FromMethodInType(method, type);

        Assert.True(ctx.TryResolve("τ_0_0", out var csName));
        Assert.Equal("T0", csName);
        // Only 1 entry — the duplicate was skipped
        Assert.Single(ctx.Mapping);
    }

    [Fact]
    public void FromMethodInType_NullType_UsesMethodOnly()
    {
        var method = CreateMethodDecl("freeFunc", new[] { "τ_0_0" });

        var ctx = GenericContext.FromMethodInType(method, null);

        Assert.True(ctx.TryResolve("τ_0_0", out var csName));
        Assert.Equal("T0", csName);
    }

    [Fact]
    public void FromMethodInType_NonGenericType_UsesMethodOnly()
    {
        var type = CreateGenericTypeDecl("Simple", Array.Empty<string>());
        var method = CreateMethodDecl("generic", new[] { "τ_0_0" });

        var ctx = GenericContext.FromMethodInType(method, type);

        Assert.True(ctx.TryResolve("τ_0_0", out var csName));
        Assert.Equal("T0", csName);
    }

    [Fact]
    public void FromMethodInType_TypeWith2Params_MethodParamStartsAtT2()
    {
        var type = CreateGenericTypeDecl("Pair", new[] { "τ_0_0", "τ_0_1" });
        var method = CreateMethodDecl("withExtra", new[] { "τ_0_0", "τ_0_1", "τ_1_0" });

        var ctx = GenericContext.FromMethodInType(method, type);

        Assert.True(ctx.TryResolve("τ_0_0", out var t0));
        Assert.Equal("T0", t0);
        Assert.True(ctx.TryResolve("τ_0_1", out var t1));
        Assert.Equal("T1", t1);
        Assert.True(ctx.TryResolve("τ_1_0", out var t2));
        Assert.Equal("T2", t2);
    }

    #endregion

    #region TupleHandler with Generic Elements

    [Fact]
    public void HasGenericTypeParameterElements_WithGenericElement_ReturnsTrue()
    {
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(new NamedTypeSpec("τ_0_0"));
        tuple.Elements.Add(new NamedTypeSpec("Swift.Int"));

        var handler = new TupleHandler(new MockTypeDatabase());
        Assert.True(handler.HasGenericTypeParameterElements(tuple));
    }

    [Fact]
    public void HasGenericTypeParameterElements_AllConcreteElements_ReturnsFalse()
    {
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(new NamedTypeSpec("Swift.Int"));
        tuple.Elements.Add(new NamedTypeSpec("Swift.Bool"));

        var handler = new TupleHandler(new MockTypeDatabase());
        Assert.False(handler.HasGenericTypeParameterElements(tuple));
    }

    [Fact]
    public void HasGenericTypeParameterElements_NestedInBoundGeneric_ReturnsTrue()
    {
        // Tuple: (Optional<τ_0_0>, Int) — generic param nested inside Optional
        var optionalWithGeneric = new NamedTypeSpec("Swift.Optional");
        optionalWithGeneric.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(optionalWithGeneric);
        tuple.Elements.Add(new NamedTypeSpec("Swift.Int"));

        var handler = new TupleHandler(new MockTypeDatabase());
        Assert.True(handler.HasGenericTypeParameterElements(tuple));
    }

    [Fact]
    public void HasGenericTypeParameterElements_ConcreteGenericElement_ReturnsFalse()
    {
        // Tuple: (Optional<Int>, Bool) — no generic params, just concrete bound generics
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(optionalInt);
        tuple.Elements.Add(new NamedTypeSpec("Swift.Bool"));

        var handler = new TupleHandler(new MockTypeDatabase());
        Assert.False(handler.HasGenericTypeParameterElements(tuple));
    }

    [Fact]
    public void IsSupportedTuple_WithGenericContext_AcceptsGenericElements()
    {
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(new NamedTypeSpec("τ_0_0"));
        tuple.Elements.Add(new NamedTypeSpec("τ_0_1"));

        var type = CreateGenericTypeDecl("Pair", new[] { "τ_0_0", "τ_0_1" });
        var ctx = GenericContext.FromType(type);

        var handler = new TupleHandler(new MockTypeDatabase());
        Assert.True(handler.IsSupportedTuple(tuple, ctx));
    }

    [Fact]
    public void IsSupportedTuple_WithoutGenericContext_ResolvesGenericElementsAsAnyType()
    {
        // Without a generic context, generic type parameters are resolved to AnyType
        // by TryGetTypeRecord (which returns AnyType for IsGenericTypeParameter names).
        // The tuple is technically "supported" but elements become AnyType.
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(new NamedTypeSpec("τ_0_0"));
        tuple.Elements.Add(new NamedTypeSpec("τ_0_1"));

        var handler = new TupleHandler(new MockTypeDatabase());
        // Returns true because TryGetTypeRecord maps generic params to AnyType
        Assert.True(handler.IsSupportedTuple(tuple));
    }

    [Fact]
    public void GetCSharpTupleType_WithGenericContext_ResolvesParams()
    {
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(new NamedTypeSpec("τ_0_0"));
        tuple.Elements.Add(new NamedTypeSpec("τ_0_1"));

        var type = CreateGenericTypeDecl("Pair", new[] { "τ_0_0", "τ_0_1" });
        var ctx = GenericContext.FromType(type);

        var handler = new TupleHandler(new MockTypeDatabase());
        var result = handler.GetCSharpTupleType(tuple, ctx);

        Assert.Equal("(T0, T1)", result);
    }

    [Fact]
    public void GetPInvokeTupleType_WithGenericContext_ResolvesToIntPtr()
    {
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(new NamedTypeSpec("τ_0_0"));
        tuple.Elements.Add(new NamedTypeSpec("τ_0_1"));

        var type = CreateGenericTypeDecl("Pair", new[] { "τ_0_0", "τ_0_1" });
        var ctx = GenericContext.FromType(type);

        var handler = new TupleHandler(new MockTypeDatabase());
        var result = handler.GetPInvokeTupleType(tuple, ctx);

        Assert.Equal("ValueTuple<IntPtr, IntPtr>", result);
    }

    #endregion

    #region BoundGenericsHandler with GenericContext

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_OptionalOfGenericParam_ResolvesCorrectly()
    {
        // Optional<τ_0_0> should resolve to SwiftOptional<T0> with the right context
        var optionalTypeSpec = new NamedTypeSpec("Swift.Optional");
        optionalTypeSpec.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));

        var type = CreateGenericTypeDecl("Wrapper", new[] { "τ_0_0" });
        var ctx = GenericContext.FromType(type);

        var handler = new BoundGenericsHandler(new MockTypeDatabase());
        var argDecl = new ArgumentDecl
        {
            SwiftTypeSpec = optionalTypeSpec,
            Name = "testArg",
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };

        var result = handler.TranslateBoundGenericTypeToCSharp(argDecl, ctx);

        Assert.Contains("SwiftOptional", result);
        Assert.Contains("T0", result);
        Assert.DoesNotContain("AnyType", result);
    }

    [Fact]
    public void TranslateBoundGenericTypeToCSharp_OptionalWithoutContext_ResolvesToAnyType()
    {
        // Optional<τ_0_0> without context should resolve the inner param to AnyType
        var optionalTypeSpec = new NamedTypeSpec("Swift.Optional");
        optionalTypeSpec.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));

        var handler = new BoundGenericsHandler(new MockTypeDatabase());
        var argDecl = new ArgumentDecl
        {
            SwiftTypeSpec = optionalTypeSpec,
            Name = "testArg",
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };

        // Without a GenericContext, the generic param resolves to AnyType
        var result = handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftOptional", result);
        Assert.Contains("AnyType", result);
    }

    #endregion

    #region Helper Methods

    private static MethodDecl CreateMethodDecl(string name, string[] genericParamNames)
    {
        var genericParams = genericParamNames.Select(n => new GenericArgumentDecl(
            TypeName: n,
            SugaredTypeName: n,
            GenericConformances: new List<GenericParameterConformance>(),
            AssosiatedTypeConformances: new List<GenericParameterConformance>()
        )).ToList();

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s_test_{name}",
            GenericParameters = genericParams,
            CSSignature = new List<ArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            MethodType = MethodType.Static,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            IsAccessor = false,
        };
    }

    private static StructDecl CreateGenericTypeDecl(string name, string[] genericParamNames)
    {
        var genericParams = genericParamNames.Select(n => new GenericArgumentDecl(
            TypeName: n,
            SugaredTypeName: n,
            GenericConformances: new List<GenericParameterConformance>(),
            AssosiatedTypeConformances: new List<GenericParameterConformance>()
        )).ToList();

        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s_test_{name}",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = genericParams,
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = $"$s_test_{name}_Ma"
        };
    }

    #endregion

    #region MockTypeDatabase

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string AsyncLibraryName => null!;

        public MockTypeDatabase()
        {
            _types = new Dictionary<string, TypeRecord>
            {
                ["Swift.Int"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Bool"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Optional"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftOptional"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Array"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftArray"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record!);
        }

        public string GetLibraryPath(string moduleName) => "";
    }

    #endregion
}
