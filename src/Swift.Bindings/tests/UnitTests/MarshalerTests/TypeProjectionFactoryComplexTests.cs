// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for TypeProjectionFactory routing of complex types — verifies the factory
/// correctly creates composite projections for generic containers, existentials,
/// tuples, closures, and async.
/// </summary>
public class TypeProjectionFactoryComplexTests
{
    private readonly TypeProjectionFactory _factory = new();

    #region Array Routing

    [Fact]
    public void Project_SwiftArray_ReturnsArrayProjection()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Bool"));
        var ctx = CreateContext(isParameter: false);

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<ArrayProjection>(projection);
        Assert.Equal("IReadOnlyList<bool>", projection.PublicType);
        Assert.Equal("IntPtr", projection.PInvokeType);
    }

    [Fact]
    public void Project_SwiftArrayOfString_ReturnsArrayWithStringElement()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.String"));
        var ctx = CreateContext(isParameter: true);

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        var arrayProj = Assert.IsType<ArrayProjection>(projection);
        Assert.Equal("IEnumerable<string>", projection.PublicType);
        Assert.IsType<StringProjection>(arrayProj.ElementProjection);
    }

    [Fact]
    public void Project_SwiftArrayOfBlittable_ReturnsArrayWithBlittableElement()
    {
        var db = new MockTypeDatabase();
        db.AddType("TestModule.Point", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        var ctx = CreateContext(db, isParameter: false);
        var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("TestModule.Point"));

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        var arrayProj = Assert.IsType<ArrayProjection>(projection);
        Assert.IsType<BlittableProjection>(arrayProj.ElementProjection);
    }

    #endregion

    #region Dictionary Routing

    [Fact]
    public void Project_SwiftDictionary_ReturnsDictionaryProjection()
    {
        var typeSpec = new NamedTypeSpec("Swift.Dictionary",
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Bool"));
        var ctx = CreateContext(isParameter: false);

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<DictionaryProjection>(projection);
        Assert.Equal("IReadOnlyDictionary<string, bool>", projection.PublicType);
    }

    [Fact]
    public void Project_SwiftDictionary_ParamType()
    {
        var typeSpec = new NamedTypeSpec("Swift.Dictionary",
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.String"));
        var ctx = CreateContext(isParameter: true);

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<DictionaryProjection>(projection);
        Assert.Equal("IDictionary<string, string>", projection.PublicType);
    }

    #endregion

    #region Optional Routing

    [Fact]
    public void Project_SwiftOptional_ReturnsOptionalProjection()
    {
        var typeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.String"));
        var ctx = CreateContext();

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<OptionalProjection>(projection);
        Assert.Equal("string?", projection.PublicType);
    }

    [Fact]
    public void Project_SwiftOptionalOfArray_ReturnsNestedProjection()
    {
        var typeSpec = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.String")));
        var ctx = CreateContext(isParameter: false);

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        var optProj = Assert.IsType<OptionalProjection>(projection);
        Assert.Equal("IReadOnlyList<string>?", projection.PublicType);
        Assert.IsType<ArrayProjection>(optProj.InnerProjection);
    }

    #endregion

    #region Existential Routing

    [Fact]
    public void Project_ProtocolListTypeSpec_ReturnsExistentialProjection()
    {
        // Single protocol: "any Describable" where Describable is in the DB
        var db = new MockTypeDatabase();
        db.AddType("TestModule.Describable", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Describable"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol
        });
        var ctx = CreateContext(db);
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Describable") });

        var projection = _factory.Project(protocolList, ctx);

        Assert.NotNull(projection);
        Assert.IsType<ExistentialProjection>(projection);
        Assert.Contains("IDescribable", projection.PublicType);
    }

    [Fact]
    public void Project_NamedTypeSpec_IsAny_ReturnsExistentialProjection()
    {
        // "any SomeProtocol" — NamedTypeSpec with IsAny=true
        var db = new MockTypeDatabase();
        db.AddType("TestModule.Sendable", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Sendable"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Sendable"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol
        });
        var ctx = CreateContext(db);
        var typeSpec = new NamedTypeSpec("TestModule.Sendable") { IsAny = true };

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<ExistentialProjection>(projection);
    }

    [Fact]
    public void Project_WellKnownProtocol_ReturnsAnyError()
    {
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") });
        var ctx = CreateContext();

        var projection = _factory.Project(protocolList, ctx);

        Assert.NotNull(projection);
        Assert.IsType<ExistentialProjection>(projection);
        Assert.Equal("Swift.AnyError", projection.PublicType);
    }

    #endregion

    #region Tuple Routing

    [Fact]
    public void Project_TupleTypeSpec_ReturnsTupleProjection()
    {
        var tupleSpec = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Bool")
        });
        var ctx = CreateContext();

        var projection = _factory.Project(tupleSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<TupleProjection>(projection);
        Assert.Equal("(string, bool)", projection.PublicType);
    }

    [Fact]
    public void Project_EmptyTuple_ReturnsNull()
    {
        var tupleSpec = new TupleTypeSpec();
        var ctx = CreateContext();

        var projection = _factory.Project(tupleSpec, ctx);
        Assert.Null(projection);
    }

    #endregion

    #region Closure Routing

    [Fact]
    public void Project_ClosureTypeSpec_ReturnsClosureProjection()
    {
        var closureSpec = new ClosureTypeSpec
        {
            Arguments = new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("Swift.String") }),
            ReturnType = new NamedTypeSpec("Swift.Bool")
        };
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var ctx = CreateContext(callbackPrefix: "test");

        var projection = _factory.Project(closureSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<ClosureProjection>(projection);
        Assert.Equal("global::System.Func<string, bool>", projection.PublicType);
    }

    [Fact]
    public void Project_VoidClosure_ReturnsAction()
    {
        var closureSpec = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = TupleTypeSpec.Empty
        };
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var ctx = CreateContext(callbackPrefix: "test");

        var projection = _factory.Project(closureSpec, ctx);

        Assert.NotNull(projection);
        var closureProj = Assert.IsType<ClosureProjection>(projection);
        Assert.Equal("global::System.Action", projection.PublicType);
    }

    #endregion

    #region Async Routing

    [Fact]
    public void Project_IsAsync_WrapsInnerInAsyncProjection()
    {
        var typeSpec = new NamedTypeSpec("Swift.String");
        var ctx = new ProjectionContext
        {
            TypeDatabase = new MockTypeDatabase(),
            IsParameter = false,
            IsAsync = true,
            Throws = false,
            CallbackNamePrefix = "test"
        };

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<AsyncProjection>(projection);
        Assert.Equal("global::System.Threading.Tasks.Task<string>", projection.PublicType);
    }

    [Fact]
    public void Project_IsAsync_WithTupleReturn_ComposesCorrectly()
    {
        var tupleSpec = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Bool")
        });
        var ctx = new ProjectionContext
        {
            TypeDatabase = new MockTypeDatabase(),
            IsParameter = false,
            IsAsync = true,
            Throws = true,
            CallbackNamePrefix = "fetchData"
        };

        var projection = _factory.Project(tupleSpec, ctx);

        Assert.NotNull(projection);
        var asyncProj = Assert.IsType<AsyncProjection>(projection);
        Assert.Equal("global::System.Threading.Tasks.Task<(string, bool)>", projection.PublicType);
        Assert.IsType<TupleProjection>(asyncProj.InnerReturnProjection);
    }

    [Fact]
    public void Project_IsAsync_VoidReturn_ReturnsTask()
    {
        var typeSpec = TupleTypeSpec.Empty;
        var ctx = new ProjectionContext
        {
            TypeDatabase = new MockTypeDatabase(),
            IsParameter = false,
            IsAsync = true,
            Throws = false,
            CallbackNamePrefix = "test"
        };

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        var asyncProj = Assert.IsType<AsyncProjection>(projection);
        Assert.Equal("global::System.Threading.Tasks.Task", projection.PublicType);
        Assert.Null(asyncProj.InnerReturnProjection);
    }

    [Fact]
    public void Project_IsAsync_VoidReturn_Throwing_ReturnsTask()
    {
        var typeSpec = TupleTypeSpec.Empty;
        var ctx = new ProjectionContext
        {
            TypeDatabase = new MockTypeDatabase(),
            IsParameter = false,
            IsAsync = true,
            Throws = true,
            CallbackNamePrefix = "test"
        };

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        var asyncProj = Assert.IsType<AsyncProjection>(projection);
        Assert.Equal("global::System.Threading.Tasks.Task", projection.PublicType);
        Assert.Equal(2, asyncProj.CallbackDeclarations.Count);
    }

    [Fact]
    public void Project_IsAsync_Parameter_DoesNotWrap()
    {
        // Async flag on a parameter should NOT wrap in AsyncProjection
        var typeSpec = new NamedTypeSpec("Swift.String");
        var ctx = new ProjectionContext
        {
            TypeDatabase = new MockTypeDatabase(),
            IsParameter = true,
            IsAsync = true
        };

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<StringProjection>(projection);
    }

    #endregion

    #region Nested Composition Routing

    [Fact]
    public void Project_NestedOptionalDictArrayString_ResolvesRecursively()
    {
        // Optional<Dictionary<String, Array<String>>>
        var typeSpec = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("Swift.Dictionary",
                new NamedTypeSpec("Swift.String"),
                new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.String"))));
        var ctx = CreateContext(isParameter: false);

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        var optProj = Assert.IsType<OptionalProjection>(projection);
        var dictProj = Assert.IsType<DictionaryProjection>(optProj.InnerProjection);
        Assert.IsType<StringProjection>(dictProj.KeyProjection);
        var arrayProj = Assert.IsType<ArrayProjection>(dictProj.ValueProjection);
        Assert.IsType<StringProjection>(arrayProj.ElementProjection);
    }

    [Fact]
    public void Project_ArrayOfDictionary_ResolvesRecursively()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array",
            new NamedTypeSpec("Swift.Dictionary",
                new NamedTypeSpec("Swift.String"),
                new NamedTypeSpec("Swift.Bool")));
        var ctx = CreateContext(isParameter: false);

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        var arrayProj = Assert.IsType<ArrayProjection>(projection);
        Assert.IsType<DictionaryProjection>(arrayProj.ElementProjection);
    }

    [Fact]
    public void Project_UnresolvableInnerElement_ReturnsNull()
    {
        // Array of an unknown type should return null
        var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Unknown.Type"));
        var ctx = CreateContext();

        var projection = _factory.Project(typeSpec, ctx);
        Assert.Null(projection);
    }

    [Fact]
    public void Project_IsAsync_UnresolvableReturn_ReturnsNull()
    {
        var typeSpec = new NamedTypeSpec("Unknown.Type");
        var ctx = new ProjectionContext
        {
            TypeDatabase = new MockTypeDatabase(),
            IsParameter = false,
            IsAsync = true
        };

        var projection = _factory.Project(typeSpec, ctx);
        Assert.Null(projection);
    }

    #endregion

    #region Backward Compatibility — Simple Types Still Work

    [Fact]
    public void Project_SwiftBool_StillWorks()
    {
        var typeSpec = new NamedTypeSpec("Swift.Bool");
        var ctx = CreateContext();

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<BoolProjection>(projection);
    }

    [Fact]
    public void Project_SwiftString_StillWorks()
    {
        var typeSpec = new NamedTypeSpec("Swift.String");
        var ctx = CreateContext();

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<StringProjection>(projection);
    }

    [Fact]
    public void Project_SimpleEnum_StillWorks()
    {
        var db = new MockTypeDatabase();
        db.AddType("TestModule.Direction", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Direction"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Direction"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            Kind = TypeRecordKind.Enum,
            RawValueTypeName = "Int32"
        });
        var ctx = CreateContext(db);
        var typeSpec = new NamedTypeSpec("TestModule.Direction");

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<SimpleEnumProjection>(projection);
    }

    #endregion

    #region Helpers

    private static ProjectionContext CreateContext(
        ITypeDatabase? db = null,
        bool isParameter = false,
        string? callbackPrefix = null)
    {
        return new ProjectionContext
        {
            TypeDatabase = db ?? new MockTypeDatabase(),
            IsParameter = isParameter,
            CallbackNamePrefix = callbackPrefix
        };
    }

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types = new();

        public void AddType(string moduleQualifiedName, TypeRecord record)
        {
            _types[moduleQualifiedName] = record;
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";
        public string? AsyncLibraryName => null;
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
