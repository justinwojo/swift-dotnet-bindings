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

    #region Concrete-class-fallback element parity (F6)

    // An unresolved concrete-class-fallback Apple type (RealityFoundation.Entity — a
    // Swift-native class with no ObjC class prefix, in a concreteClassFallback-only module)
    // projects via ClassProjection through the Optional fallback path, but element projection
    // for Array/Set/Dictionary used to consult only the IsOptionalFallbackModule +
    // HasObjCClassPrefix heuristics. That asymmetry silently dropped any member typed
    // [Entity] / Set<Entity> / [K: Entity] (e.g. RealityFoundation's entitiesTargetedByATapTrigger
    // property and the append(contentsOf: [Entity]) overload) while Optional<Entity> projected
    // fine. These tests pin element parity: the same concrete-class fallback applies inside
    // collections, yielding a ClassProjection element — the already-runtime-proven shape used
    // for any local Swift-class element ([Animal], [TrackedRef]).
    //
    // An empty TypeDatabase leaves Entity unresolved, forcing the registry-keyed fallback
    // (IsConcreteClassFallbackModule), which is the exact reach in apple-framework generation.

    [Fact]
    public void Project_OptionalOfConcreteClassFallback_ProjectsViaClassProjection()
    {
        // Baseline: the Optional path already handles the concrete-class fallback. This is the
        // working half of the asymmetry the element tests below close.
        var typeSpec = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("RealityFoundation.Entity"));
        var ctx = CreateContext(isParameter: false);

        var projection = _factory.Project(typeSpec, ctx);

        var optProj = Assert.IsType<OptionalProjection>(projection);
        Assert.IsType<ClassProjection>(optProj.InnerProjection);
    }

    [Fact]
    public void Project_ArrayOfConcreteClassFallback_ProjectsElementViaClassProjection()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array",
            new NamedTypeSpec("RealityFoundation.Entity"));
        var ctx = CreateContext(isParameter: true);

        var projection = _factory.Project(typeSpec, ctx);

        var arrayProj = Assert.IsType<ArrayProjection>(projection);
        Assert.IsType<ClassProjection>(arrayProj.ElementProjection);
        Assert.Equal("RealityFoundation.Entity", arrayProj.ElementProjection.PublicType);
    }

    [Fact]
    public void Project_SetOfConcreteClassFallback_ProjectsElementViaClassProjection()
    {
        var typeSpec = new NamedTypeSpec("Swift.Set",
            new NamedTypeSpec("RealityFoundation.Entity"));
        var ctx = CreateContext(isParameter: false);

        var projection = _factory.Project(typeSpec, ctx);

        var setProj = Assert.IsType<SetProjection>(projection);
        Assert.IsType<ClassProjection>(setProj.ElementProjection);
    }

    [Fact]
    public void Project_DictionaryWithConcreteClassFallbackValue_ProjectsValueViaClassProjection()
    {
        var typeSpec = new NamedTypeSpec("Swift.Dictionary",
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("RealityFoundation.Entity"));
        var ctx = CreateContext(isParameter: false);

        var projection = _factory.Project(typeSpec, ctx);

        var dictProj = Assert.IsType<DictionaryProjection>(projection);
        Assert.IsType<ClassProjection>(dictProj.ValueProjection);
    }

    [Fact]
    public void Project_DictionaryWithConcreteClassFallbackKey_ProjectsKeyViaClassProjection()
    {
        // The Dictionary KEY leg routes through TryProjectObjCElement via its own `??`
        // call site, distinct from the value leg above — pin it too so the symmetric
        // fix can't regress on one position while the other stays green.
        var typeSpec = new NamedTypeSpec("Swift.Dictionary",
            new NamedTypeSpec("RealityFoundation.Entity"),
            new NamedTypeSpec("Swift.String"));
        var ctx = CreateContext(isParameter: false);

        var projection = _factory.Project(typeSpec, ctx);

        var dictProj = Assert.IsType<DictionaryProjection>(projection);
        Assert.IsType<ClassProjection>(dictProj.KeyProjection);
    }

    [Fact]
    public void Project_ArrayOfDualModuleNonObjCPrefixedClass_ProjectsViaClassProjection()
    {
        // The real-world reach: RealityKit.Entity, not RealityFoundation.Entity. RealityKit is
        // a DUAL-flagged module (optionalFallback AND concreteClassFallback) with an "RE" ObjC
        // prefix, whereas RealityFoundation is concrete-only. The ObjC element branch runs first
        // and is gated on IsOptionalFallbackModule + HasObjCClassPrefix — RealityKit satisfies
        // the former, so the ONLY reason "Entity" falls through to the concrete-class branch is
        // that "Entity" doesn't start with "RE". This pins that precedence: a regression in the
        // ObjC-prefix check or branch ordering would silently re-route Entity (or drop it), and
        // the concrete-only RealityFoundation.Entity test above would not catch it. The validate
        // sweep confirms this exact element type reaches real members
        // (Entity.ChildCollection.Append/ReplaceAll take [RealityKit.Entity]).
        var typeSpec = new NamedTypeSpec("Swift.Array",
            new NamedTypeSpec("RealityKit.Entity"));
        var ctx = CreateContext(isParameter: true);

        var projection = _factory.Project(typeSpec, ctx);

        var arrayProj = Assert.IsType<ArrayProjection>(projection);
        Assert.IsType<ClassProjection>(arrayProj.ElementProjection);
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
        Assert.Equal("Swift.Foundation.AnyError", projection.PublicType);
    }

    [Fact]
    public void Project_MarkerOnlyExistential_TakesBareAnyBoxUnboxArm()
    {
        // `any Sendable` is marker-only → zero-witness → ABI-identical to bare Any. The factory
        // must set isBareAny so the projection marshals via ExistentialContainer0.Box/Unbox
        // (the object Box/Unbox path), NOT a proxy / ISwiftExistentialConvertible arm — those
        // cannot apply to the `object` public surface a marker-only existential degrades to.
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sendable") });
        var ctx = CreateContext();

        var projection = _factory.Project(protocolList, ctx);

        var existential = Assert.IsType<ExistentialProjection>(projection);
        Assert.Equal("object", existential.PublicType);
        // Box/Unbox arm selected, exactly as for bare Any.
        Assert.Contains("ExistentialContainer0.Box", existential.GetParameterPlan("p").PInvokeExpression);
        Assert.Contains("ExistentialContainer0.Unbox",
            existential.GetReturnPlan("r", ReturnStrategy.Direct).PInvokeExpression);
    }

    [Fact]
    public void Project_BareAnyAndMarkerOnly_ProduceIdenticalMarshalling()
    {
        // Container-level proof that the two source spellings collapse to the SAME ABI marshalling:
        // bare `Any` (0 protocols) and `any Sendable` (marker-only) must yield identical Box/Unbox
        // parameter and return expressions.
        var ctx = CreateContext();
        var bareAny = Assert.IsType<ExistentialProjection>(_factory.Project(new ProtocolListTypeSpec(), ctx));
        var markerOnly = Assert.IsType<ExistentialProjection>(
            _factory.Project(new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sendable") }), ctx));

        Assert.Equal(bareAny.GetParameterPlan("p").PInvokeExpression,
            markerOnly.GetParameterPlan("p").PInvokeExpression);
        Assert.Equal(bareAny.GetReturnPlan("r", ReturnStrategy.Direct).PInvokeExpression,
            markerOnly.GetReturnPlan("r", ReturnStrategy.Direct).PInvokeExpression);
    }

    [Fact]
    public void Project_PATProtocolWithConformers_FactoryPath_ReturnsObject_NotExistentialUnion()
    {
        // S12 inert-engine pin. TypeProjectionFactory.ProjectExistential calls
        // GetPublicExistentialType WITHOUT allowUnionProjection:true,
        // so a PAT/Self-constrained protocol that DOES have known conformers must still project to
        // "object" on the factory path — never to Swift.Runtime.ExistentialUnion. The union surface
        // is reachable only via the env-path oracle (a pure-read return position). If anyone threads
        // allowUnionProjection:true into the factory, this test goes red.
        var db = new MockTypeDatabase();
        db.AddType("SwiftBindingsTestLib.AttributeKind", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("SwiftBindingsTestLib", "IAttributeKind"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.AttributeKind"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.HasAssociatedTypes, // PAT — degrades to object/union
            Kind = TypeRecordKind.Protocol
        });
        // Engine carries AttributeKind conformers from the specialization-hints ledger.
        var engine = new ConcreteSpecializationEngine(db);
        var protocolList = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("SwiftBindingsTestLib.AttributeKind") });

        // Guard: with the union flag set, the SAME protocol + engine genuinely yields ExistentialUnion,
        // proving the conformers ARE present — so the factory's "object" below is the position gate at
        // work, not a conformer-absence artifact.
        var probe = new ExistentialHandler(db) { SpecializationEngine = engine };
        Assert.Equal("Swift.Runtime.ExistentialUnion",
            probe.GetPublicExistentialType(protocolList, allowUnionProjection: true));

        var ctx = new ProjectionContext
        {
            TypeDatabase = db,
            IsParameter = false,
            CurrentModuleName = "SwiftBindingsTestLib",
            SpecializationEngine = engine,
        };

        var projection = _factory.Project(protocolList, ctx);

        var existential = Assert.IsType<ExistentialProjection>(projection);
        Assert.Equal("object", existential.PublicType);
    }

    [Theory]
    // The proxy-unavailability predicate ORs HasSelfRequirement and HasAssociatedTypes, so both
    // arms must be pinned (real PATs carry HasAssociatedTypes; Self-constrained stdlib protocols
    // carry HasSelfRequirement).
    [InlineData(TypeRecordFlags.HasSelfRequirement)]
    [InlineData(TypeRecordFlags.HasAssociatedTypes)]
    public void Project_ConstrainedSelfATExistential_ProduceFailsClosed(TypeRecordFlags selfOrAssociatedFlag)
    {
        // Constrained existential `any Pipeline<Int>` for a Self/associated-type protocol: the
        // public surface does NOT collapse to `object` (GetPublicExistentialType resolves the
        // closed-form `IPipeline<nint>`), yet ProtocolProxyEmitter never writes a `PipelineProxy`
        // class for a Self/AT protocol — and a foreign/stdlib protocol is never in the module's
        // suppressed-proxy name set. The projection's PRODUCE arms must therefore fail closed
        // (throw so the member-emit boundary re-stubs the member) instead of shipping a
        // `new PipelineProxy(…)` reference to a class that is never emitted (dangling CS0246).
        var db = new MockTypeDatabase();
        db.AddType("TestModule.Pipeline", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IPipeline"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MetadataAccessor = "",
            Flags = selfOrAssociatedFlag,
            Kind = TypeRecordKind.Protocol
        });
        db.AddType("Swift.Int", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.NIntType,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        var protocolList = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.Pipeline", new TypeSpec[] { new NamedTypeSpec("Swift.Int") })
        });
        var ctx = new ProjectionContext
        {
            TypeDatabase = db,
            IsParameter = false,
            CurrentModuleName = "TestModule",
            EmissionContext = new ModuleEmissionContext()
        };

        var projection = _factory.Project(protocolList, ctx);

        var existential = Assert.IsType<ExistentialProjection>(projection);
        // Sanity: this is the non-collapsing residual — the `object` degradation does NOT shadow it.
        Assert.Equal("IPipeline<nint>", existential.PublicType);
        Assert.Throws<SuppressedProxyReferenceException>(
            () => existential.GetReturnPlan("result", ReturnStrategy.Direct));
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
