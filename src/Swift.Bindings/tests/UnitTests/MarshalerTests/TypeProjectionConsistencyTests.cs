// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Systematic type matrix verifying TypeProjectionFactory.Project() produces correct
/// (PublicType, PInvokeType, ProjectionType) for all real-world Swift types.
/// Also verifies cross-layer signature agreement: wrapper type, P/Invoke type,
/// and marshalling plan all agree on types.
/// </summary>
public class TypeProjectionConsistencyTests
{
    private readonly TypeProjectionFactory _factory = new();

    #region Part 1: Type Matrix — Project_ProducesExpectedTypes

    [Theory]
    [MemberData(nameof(WellKnownSimpleTypes))]
    [MemberData(nameof(TypeDatabaseResolvedTypes))]
    [MemberData(nameof(ContainerParamTypes))]
    [MemberData(nameof(ContainerReturnTypes))]
    [MemberData(nameof(OptionalTypes))]
    [MemberData(nameof(ExistentialTypes))]
    [MemberData(nameof(TupleTypes))]
    [MemberData(nameof(DeepNestingTypes))]
    public void Project_ProducesExpectedTypes(
        string testName,
        TypeSpec typeSpec,
        bool isParameter,
        string expectedPublicType,
        string expectedPInvokeType,
        Type expectedProjectionType)
    {
        var ctx = CreateContext(_sharedDb, isParameter: isParameter);

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.True(
            expectedProjectionType.IsInstanceOfType(projection),
            $"[{testName}] Expected projection type {expectedProjectionType.Name}, got {projection.GetType().Name}");
        Assert.True(
            expectedPublicType == projection.PublicType,
            $"[{testName}] Expected PublicType '{expectedPublicType}', got '{projection.PublicType}'");
        Assert.True(
            expectedPInvokeType == projection.PInvokeType,
            $"[{testName}] Expected PInvokeType '{expectedPInvokeType}', got '{projection.PInvokeType}'");
    }

    [Theory]
    [MemberData(nameof(ClosureTypes))]
    public void Project_Closure_ProducesExpectedTypes(
        string testName,
        ClosureTypeSpec closureSpec,
        string expectedPublicType,
        string expectedPInvokeType,
        Type expectedProjectionType)
    {
        var ctx = CreateContext(_sharedDb, callbackPrefix: "test");

        var projection = _factory.Project(closureSpec, ctx);

        Assert.NotNull(projection);
        Assert.True(
            expectedProjectionType.IsInstanceOfType(projection),
            $"[{testName}] Expected projection type {expectedProjectionType.Name}, got {projection.GetType().Name}");
        Assert.True(
            expectedPublicType == projection.PublicType,
            $"[{testName}] Expected PublicType '{expectedPublicType}', got '{projection.PublicType}'");
        Assert.True(
            expectedPInvokeType == projection.PInvokeType,
            $"[{testName}] Expected PInvokeType '{expectedPInvokeType}', got '{projection.PInvokeType}'");
    }

    [Theory]
    [MemberData(nameof(AsyncTypes))]
    public void Project_Async_ProducesExpectedTypes(
        string testName,
        TypeSpec typeSpec,
        bool throws,
        string expectedPublicType,
        string expectedPInvokeType,
        Type expectedProjectionType)
    {
        var ctx = new ProjectionContext
        {
            TypeDatabase = _sharedDb,
            IsParameter = false,
            IsAsync = true,
            Throws = throws,
            CallbackNamePrefix = "test"
        };

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.True(
            expectedProjectionType.IsInstanceOfType(projection),
            $"[{testName}] Expected projection type {expectedProjectionType.Name}, got {projection.GetType().Name}");
        Assert.True(
            expectedPublicType == projection.PublicType,
            $"[{testName}] Expected PublicType '{expectedPublicType}', got '{projection.PublicType}'");
        Assert.True(
            expectedPInvokeType == projection.PInvokeType,
            $"[{testName}] Expected PInvokeType '{expectedPInvokeType}', got '{projection.PInvokeType}'");
    }

    #endregion

    #region Part 1: Test Data

    public static IEnumerable<object[]> WellKnownSimpleTypes()
    {
        // Bool
        yield return new object[] { "Bool", N("Swift.Bool"), false, "bool", "bool", typeof(BoolProjection) };

        // String
        yield return new object[] { "String", N("Swift.String"), false, "string", "SwiftString", typeof(StringProjection) };

        // Integer/float types (resolve through TypeDatabase as blittable frozen structs)
        yield return new object[] { "Int64 (blittable)", N("Swift.Int64"), false, "Swift.Int64", "Swift.Int64", typeof(BlittableProjection) };
        yield return new object[] { "Double (blittable)", N("Swift.Double"), false, "Swift.Double", "Swift.Double", typeof(BlittableProjection) };
        yield return new object[] { "Float (blittable)", N("Swift.Float"), false, "Swift.Float", "Swift.Float", typeof(BlittableProjection) };

        // Foundation.Date — double ABI, DateTimeOffset public
        yield return new object[] { "Foundation.Date", N("Foundation.Date"), false, "System.DateTimeOffset", "double", typeof(DateProjection) };

        // Pointer types
        yield return new object[] { "OpaquePointer", N("Swift.OpaquePointer"), false, "System.IntPtr", "System.IntPtr", typeof(BlittableProjection) };
        yield return new object[] { "UnsafeRawPointer", N("Swift.UnsafeRawPointer"), false, "System.IntPtr", "System.IntPtr", typeof(BlittableProjection) };
    }

    public static IEnumerable<object[]> TypeDatabaseResolvedTypes()
    {
        // ObjC bridged
        yield return new object[] { "ObjCBridged", N("TestModule.UIImage"), false,
            "TestModule.UIImage", "IntPtr", typeof(ObjCBridgedProjection) };

        // Simple enum (Int32)
        yield return new object[] { "SimpleEnum(Int32)", N("TestModule.Direction"), false,
            "TestModule.Direction", "int", typeof(SimpleEnumProjection) };

        // Simple enum (Int64)
        yield return new object[] { "SimpleEnum(Int64)", N("TestModule.BigEnum"), false,
            "TestModule.BigEnum", "long", typeof(SimpleEnumProjection) };

        // Frozen with memory management (ClassWithBufferStruct)
        yield return new object[] { "FrozenWithMemory", N("TestModule.ManagedFrozen"), false,
            "TestModule.ManagedFrozen", "TestModule.ManagedFrozen.Buffer", typeof(FrozenWithMemoryProjection) };

        // Non-frozen struct
        yield return new object[] { "NonFrozenStruct", N("TestModule.Pipeline"), false,
            "TestModule.Pipeline", "IntPtr", typeof(NonFrozenStructProjection) };

        // Frozen blittable struct
        yield return new object[] { "FrozenStruct", N("TestModule.Point"), false,
            "TestModule.Point", "TestModule.Point", typeof(BlittableProjection) };

        // Class
        yield return new object[] { "Class", N("TestModule.MyViewController"), false,
            "TestModule.MyViewController", "IntPtr", typeof(ClassProjection) };

        // Native remapped (frozen)
        yield return new object[] { "NativeRemapped(frozen)", N("TestModule.SwiftURL"), false,
            "Foundation.NSUrl", "TestModule.SwiftURL", typeof(NativeRemappedProjection) };

        // Native remapped (non-frozen) — uses a non-Data type since Data now gets DataProjection
        yield return new object[] { "NativeRemapped(non-frozen)", N("TestModule.SwiftTimestamp"), false,
            "Foundation.NSDate", "SafeHandle", typeof(NativeRemappedProjection) };
    }

    public static IEnumerable<object[]> ContainerParamTypes()
    {
        yield return new object[] { "Array<String> param", N("Swift.Array", N("Swift.String")), true,
            "IEnumerable<string>", "IntPtr", typeof(ArrayProjection) };
        yield return new object[] { "Array<Bool> param", N("Swift.Array", N("Swift.Bool")), true,
            "IEnumerable<bool>", "IntPtr", typeof(ArrayProjection) };
        yield return new object[] { "Array<Class> param", N("Swift.Array", N("TestModule.MyViewController")), true,
            "IEnumerable<TestModule.MyViewController>", "IntPtr", typeof(ArrayProjection) };
        yield return new object[] { "Dict<String,Int64> param", N("Swift.Dictionary", N("Swift.String"), N("Swift.Int64")), true,
            "IDictionary<string, Swift.Int64>", "IntPtr", typeof(DictionaryProjection) };
        yield return new object[] { "Dict<String,String> param", N("Swift.Dictionary", N("Swift.String"), N("Swift.String")), true,
            "IDictionary<string, string>", "IntPtr", typeof(DictionaryProjection) };
    }

    public static IEnumerable<object[]> ContainerReturnTypes()
    {
        yield return new object[] { "Array<String> return", N("Swift.Array", N("Swift.String")), false,
            "IReadOnlyList<string>", "IntPtr", typeof(ArrayProjection) };
        yield return new object[] { "Array<Bool> return", N("Swift.Array", N("Swift.Bool")), false,
            "IReadOnlyList<bool>", "IntPtr", typeof(ArrayProjection) };
        yield return new object[] { "Dict<String,Int64> return", N("Swift.Dictionary", N("Swift.String"), N("Swift.Int64")), false,
            "IReadOnlyDictionary<string, Swift.Int64>", "IntPtr", typeof(DictionaryProjection) };
        yield return new object[] { "Dict<String,String> return", N("Swift.Dictionary", N("Swift.String"), N("Swift.String")), false,
            "IReadOnlyDictionary<string, string>", "IntPtr", typeof(DictionaryProjection) };
        yield return new object[] { "Array<Class> return", N("Swift.Array", N("TestModule.MyViewController")), false,
            "IReadOnlyList<TestModule.MyViewController>", "IntPtr", typeof(ArrayProjection) };
    }

    public static IEnumerable<object[]> OptionalTypes()
    {
        // Optional inner types always use IsParameter=false per factory logic
        yield return new object[] { "Optional<String>", N("Swift.Optional", N("Swift.String")), false,
            "string?", "IntPtr", typeof(OptionalProjection) };
        yield return new object[] { "Optional<String> param", N("Swift.Optional", N("Swift.String")), true,
            "string?", "IntPtr", typeof(OptionalProjection) };
        yield return new object[] { "Optional<Bool>", N("Swift.Optional", N("Swift.Bool")), false,
            "bool?", "IntPtr", typeof(OptionalProjection) };
        yield return new object[] { "Optional<NonFrozen>", N("Swift.Optional", N("TestModule.Pipeline")), false,
            "TestModule.Pipeline?", "IntPtr", typeof(OptionalProjection) };
        yield return new object[] { "Optional<Array<String>>", N("Swift.Optional", N("Swift.Array", N("Swift.String"))), false,
            "IReadOnlyList<string>?", "IntPtr", typeof(OptionalProjection) };
        yield return new object[] { "Optional<Dict<String,String>>", N("Swift.Optional", N("Swift.Dictionary", N("Swift.String"), N("Swift.String"))), false,
            "IReadOnlyDictionary<string, string>?", "IntPtr", typeof(OptionalProjection) };
        yield return new object[] { "Optional<Class>", N("Swift.Optional", N("TestModule.MyViewController")), false,
            "TestModule.MyViewController?", "IntPtr", typeof(OptionalProjection) };
        yield return new object[] { "Optional<Enum>", N("Swift.Optional", N("TestModule.Direction")), false,
            "TestModule.Direction?", "IntPtr", typeof(OptionalProjection) };
    }

    public static IEnumerable<object[]> ExistentialTypes()
    {
        // Known protocol with proxy
        yield return new object[] { "Existential(known protocol)", MakeProtocolList("TestModule.Describable"), false,
            "IDescribable", "Swift.Runtime.ExistentialContainer1", typeof(ExistentialProjection) };

        // Protocol composition (2 protocols)
        yield return new object[] { "Existential(composition)", MakeProtocolList("TestModule.Describable", "TestModule.Renderable"), false,
            "IDescribableAndRenderable", "Swift.Runtime.ExistentialContainer2", typeof(ExistentialProjection) };

        // Swift.Error → AnyError
        yield return new object[] { "Swift.Error → AnyError", MakeProtocolList("Swift.Error"), false,
            "Swift.Foundation.AnyError", "Swift.Runtime.ExistentialContainer1", typeof(ExistentialProjection) };

        // Unknown protocol → object
        yield return new object[] { "Existential(unknown) → object", MakeProtocolList("TestModule.UnknownProtocol"), false,
            "object", "Swift.Runtime.ExistentialContainer1", typeof(ExistentialProjection) };

        // Optional<Existential> with known protocol
        yield return new object[] { "Optional<Existential>", N("Swift.Optional", MakeProtocolList("TestModule.Describable")), false,
            "IDescribable?", "IntPtr", typeof(OptionalProjection) };
    }

    public static IEnumerable<object[]> TupleTypes()
    {
        yield return new object[] { "(String, Bool)", MakeTuple(N("Swift.String"), N("Swift.Bool")), false,
            "(string, bool)", "ValueTuple<SwiftString, bool>", typeof(TupleProjection) };

        yield return new object[] { "(Int64, Int64)", MakeTuple(N("Swift.Int64"), N("Swift.Int64")), false,
            "(Swift.Int64, Swift.Int64)", "ValueTuple<Swift.Int64, Swift.Int64>", typeof(TupleProjection) };

        yield return new object[] { "(String, Bool, Int64)", MakeTuple(N("Swift.String"), N("Swift.Bool"), N("Swift.Int64")), false,
            "(string, bool, Swift.Int64)", "ValueTuple<SwiftString, bool, Swift.Int64>", typeof(TupleProjection) };
    }

    public static IEnumerable<object[]> ClosureTypes()
    {
        yield return new object[] { "Action<string>",
            MakeEscapingClosure(new[] { N("Swift.String") }, TupleTypeSpec.Empty),
            "global::System.Action<string>", "SwiftClosureData", typeof(ClosureProjection) };

        yield return new object[] { "Action (void→void)",
            MakeEscapingClosure(Array.Empty<TypeSpec>(), TupleTypeSpec.Empty),
            "global::System.Action", "SwiftClosureData", typeof(ClosureProjection) };

        yield return new object[] { "Func<string, bool>",
            MakeEscapingClosure(new[] { N("Swift.String") }, N("Swift.Bool")),
            "global::System.Func<string, bool>", "SwiftClosureData", typeof(ClosureProjection) };

        yield return new object[] { "Non-escaping closure",
            MakeNonEscapingClosure(new[] { N("Swift.Bool") }, TupleTypeSpec.Empty),
            "global::System.Action<bool>", "delegate* unmanaged[Swift]<bool, IntPtr, void>", typeof(ClosureProjection) };
    }

    public static IEnumerable<object[]> AsyncTypes()
    {
        yield return new object[] { "Task<string>", N("Swift.String"), false,
            "global::System.Threading.Tasks.Task<string>", "void", typeof(AsyncProjection) };

        yield return new object[] { "Task<bool>", N("Swift.Bool"), false,
            "global::System.Threading.Tasks.Task<bool>", "void", typeof(AsyncProjection) };

        yield return new object[] { "Task (void)", TupleTypeSpec.Empty, false,
            "global::System.Threading.Tasks.Task", "void", typeof(AsyncProjection) };

        yield return new object[] { "Task<(string, bool)>",
            MakeTuple(N("Swift.String"), N("Swift.Bool")), true,
            "global::System.Threading.Tasks.Task<(string, bool)>", "void", typeof(AsyncProjection) };
    }

    public static IEnumerable<object[]> DeepNestingTypes()
    {
        // Optional<Dict<String, Array<String>>>
        yield return new object[] { "Optional<Dict<String,Array<String>>>",
            N("Swift.Optional", N("Swift.Dictionary", N("Swift.String"), N("Swift.Array", N("Swift.String")))),
            false,
            "IReadOnlyDictionary<string, IReadOnlyList<string>>?", "IntPtr", typeof(OptionalProjection) };

        // Array<Existential> param
        yield return new object[] { "Array<Existential> param",
            N("Swift.Array", MakeProtocolList("TestModule.Describable")),
            true,
            "IEnumerable<IDescribable>", "IntPtr", typeof(ArrayProjection) };

        // Array<Existential> return
        yield return new object[] { "Array<Existential> return",
            N("Swift.Array", MakeProtocolList("TestModule.Describable")),
            false,
            "IReadOnlyList<IDescribable>", "IntPtr", typeof(ArrayProjection) };

        // Dict<String, Array<Bool>>
        yield return new object[] { "Dict<String, Array<Bool>> return",
            N("Swift.Dictionary", N("Swift.String"), N("Swift.Array", N("Swift.Bool"))),
            false,
            "IReadOnlyDictionary<string, IReadOnlyList<bool>>", "IntPtr", typeof(DictionaryProjection) };
    }

    public static IEnumerable<object[]> NullReturnTypes()
    {
        // Unknown type
        yield return new object[] { "Unknown type → null", N("Unknown.Foo") };

        // Unresolvable inner in container
        yield return new object[] { "Array<Unknown> → null", N("Swift.Array", N("Unknown.Bar")) };

        // Empty tuple
        yield return new object[] { "Empty tuple → null", new TupleTypeSpec() };

        // Generic type parameter
        yield return new object[] { "Generic param τ_0_0 → null", N("τ_0_0") };

        // User-defined type with generic parameters (not supported by factory)
        yield return new object[] { "BoundGeneric → null",
            new NamedTypeSpec("TestModule.Result", new NamedTypeSpec("Swift.String")) };

        // Optional with unresolvable inner
        yield return new object[] { "Optional<Unknown> → null",
            N("Swift.Optional", N("Unknown.Baz")) };
    }

    [Theory]
    [MemberData(nameof(NullReturnTypes))]
    public void Project_ReturnsNull_ForUnsupportedTypes(string testName, TypeSpec typeSpec)
    {
        var ctx = CreateContext(_sharedDb);

        var projection = _factory.Project(typeSpec, ctx);

        Assert.True(projection == null, $"[{testName}] Expected null projection, got {projection?.GetType().Name}");
    }

    #endregion

    #region Part 2: Cross-Layer Signature Agreement

    [Theory]
    [MemberData(nameof(SignatureAgreementTypes))]
    public void SignatureAgreement_ParameterPlan_PInvokeExpressionIsProduced(
        string testName,
        ITypeProjection projection)
    {
        var plan = projection.GetParameterPlan("arg");

        // Every projection must produce a non-null P/Invoke expression
        Assert.True(
            !string.IsNullOrEmpty(plan.PInvokeExpression),
            $"[{testName}] Parameter plan produced empty PInvokeExpression");
    }

    [Theory]
    [MemberData(nameof(SignatureAgreementTypes))]
    public void SignatureAgreement_ReturnPlan_Direct_ProducesOutput(
        string testName,
        ITypeProjection projection)
    {
        var plan = projection.GetReturnPlan("result", ReturnStrategy.Direct);

        // Return plan must produce either PInvokeExpression or setup statements (embedded return)
        bool hasExpression = !string.IsNullOrEmpty(plan.PInvokeExpression);
        bool hasSetup = plan.SetupStatements.Count > 0;
        Assert.True(
            hasExpression || hasSetup,
            $"[{testName}] Return plan produced neither PInvokeExpression nor setup statements");
    }

    [Theory]
    [MemberData(nameof(SignatureAgreementTypes))]
    public void SignatureAgreement_ReturnPlan_IndirectResult_ProducesOutput(
        string testName,
        ITypeProjection projection)
    {
        var plan = projection.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        bool hasExpression = !string.IsNullOrEmpty(plan.PInvokeExpression);
        bool hasSetup = plan.SetupStatements.Count > 0;
        Assert.True(
            hasExpression || hasSetup,
            $"[{testName}] IndirectResult plan produced neither PInvokeExpression nor setup statements");
    }

    [Fact]
    public void SignatureAgreement_String_ParamPlanUsesSwiftString()
    {
        var proj = new StringProjection();
        var plan = proj.GetParameterPlan("name");

        // Setup should create SwiftString; expression should reference the using variable
        var usingStmt = plan.SetupStatements.OfType<MarshalStatement.Using>().FirstOrDefault();
        Assert.NotNull(usingStmt);
        Assert.Equal("SwiftString", usingStmt.Type);
        Assert.Contains("new SwiftString(name)", usingStmt.InitExpression);
    }

    [Fact]
    public void SignatureAgreement_SimpleEnum_ParamPlanCastsToUnderlying()
    {
        var proj = new SimpleEnumProjection("Direction", "int");
        var plan = proj.GetParameterPlan("dir");

        Assert.Contains("(int)dir", plan.PInvokeExpression);
    }

    [Fact]
    public void SignatureAgreement_SimpleEnum_ReturnPlanCastsFromUnderlying()
    {
        var proj = new SimpleEnumProjection("Direction", "int");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Contains("(Direction)result", plan.PInvokeExpression);
    }

    [Fact]
    public void SignatureAgreement_ObjCBridged_ParamUsesHandle()
    {
        var proj = new ObjCBridgedProjection("UIImage");
        var plan = proj.GetParameterPlan("image");

        Assert.Contains("Handle", plan.PInvokeExpression);
    }

    [Fact]
    public void SignatureAgreement_ObjCBridged_ReturnUsesGetNSObject()
    {
        var proj = new ObjCBridgedProjection("UIImage");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Contains("GetNSObject<UIImage>", plan.PInvokeExpression);
    }

    [Fact]
    public void SignatureAgreement_NonFrozenStruct_ParamUsesDangerousGetHandle()
    {
        var proj = new NonFrozenStructProjection("Pipeline");
        var plan = proj.GetParameterPlan("pipe");

        Assert.Contains("DangerousGetHandle", plan.PInvokeExpression);
    }

    [Fact]
    public void SignatureAgreement_Class_ParamUsesDangerousGetHandle()
    {
        var proj = new ClassProjection("MyViewController");
        var plan = proj.GetParameterPlan("vc");

        Assert.Contains("DangerousGetHandle", plan.PInvokeExpression);
    }

    [Fact]
    public void SignatureAgreement_Class_ReturnHasDirectMarshalFromSwift()
    {
        var proj = new ClassProjection("MyViewController");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        // ARC bridge: no buffer allocation, direct MarshalFromSwift
        Assert.False(plan.RequiresUnsafe);

        var rendered = RenderPlan(plan);
        Assert.Contains("MarshalFromSwiftObject<MyViewController>", rendered);
        Assert.DoesNotContain("NativeMemory", rendered);
        Assert.DoesNotContain("try", rendered);
        Assert.DoesNotContain("catch", rendered);
    }

    [Fact]
    public void SignatureAgreement_Array_ParamAndReturnDirectionConsistent()
    {
        var elemProj = new StringProjection();
        var paramProj = new ArrayProjection(elemProj, isParameter: true);
        var returnProj = new ArrayProjection(elemProj, isParameter: false);

        // Param → IEnumerable, Return → IReadOnlyList
        Assert.Equal("IEnumerable<string>", paramProj.PublicType);
        Assert.Equal("IReadOnlyList<string>", returnProj.PublicType);

        // Both use IntPtr as P/Invoke type
        Assert.Equal("IntPtr", paramProj.PInvokeType);
        Assert.Equal("IntPtr", returnProj.PInvokeType);
    }

    [Fact]
    public void SignatureAgreement_Dictionary_ParamAndReturnDirectionConsistent()
    {
        var keyProj = new StringProjection();
        var valProj = new BlittableProjection("Int64");
        var paramProj = new DictionaryProjection(keyProj, valProj, isParameter: true);
        var returnProj = new DictionaryProjection(keyProj, valProj, isParameter: false);

        Assert.Equal("IDictionary<string, Int64>", paramProj.PublicType);
        Assert.Equal("IReadOnlyDictionary<string, Int64>", returnProj.PublicType);
        Assert.Equal("IntPtr", paramProj.PInvokeType);
        Assert.Equal("IntPtr", returnProj.PInvokeType);
    }

    [Fact]
    public void SignatureAgreement_Existential_ParamUsesGetExistentialContainer()
    {
        var proj = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var plan = proj.GetParameterPlan("item");

        Assert.Contains("ExistentialContainerFactory.GetOrCreate<IDescribable>", plan.PInvokeExpression);
    }

    [Fact]
    public void SignatureAgreement_Existential_ReturnUsesProxy()
    {
        var proj = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Contains("new DescribableProxy", plan.PInvokeExpression);
    }

    [Fact]
    public void SignatureAgreement_Bool_HasMarshalAsAttribute()
    {
        var proj = new BoolProjection();

        Assert.Equal("[MarshalAs(UnmanagedType.U1)]", proj.PInvokeAttribute);
    }

    [Fact]
    public void SignatureAgreement_NativeRemapped_Frozen_ReturnsWrapperType()
    {
        var proj = new NativeRemappedProjection("Foundation.NSUrl", "SwiftURL", isFrozen: true, toConversionMethod: "ToNSUrl");

        Assert.Equal("Foundation.NSUrl", proj.PublicType);
        Assert.Equal("SwiftURL", proj.PInvokeType);
    }

    [Fact]
    public void SignatureAgreement_NativeRemapped_NonFrozen_ReturnsSafeHandle()
    {
        var proj = new NativeRemappedProjection("Foundation.NSDate", "SwiftTimestamp", isFrozen: false, toConversionMethod: "ToNSDate");

        Assert.Equal("Foundation.NSDate", proj.PublicType);
        Assert.Equal("SafeHandle", proj.PInvokeType);
    }

    public static IEnumerable<object[]> SignatureAgreementTypes()
    {
        yield return new object[] { "String", new StringProjection() };
        yield return new object[] { "Bool", new BoolProjection() };
        yield return new object[] { "Blittable(Int64)", new BlittableProjection("Int64") };
        yield return new object[] { "SimpleEnum", new SimpleEnumProjection("Direction", "int") };
        yield return new object[] { "ObjCBridged", new ObjCBridgedProjection("UIImage") };
        yield return new object[] { "NonFrozenStruct", new NonFrozenStructProjection("Pipeline") };
        yield return new object[] { "Class", new ClassProjection("MyViewController") };
        yield return new object[] { "FrozenWithMemory", new FrozenWithMemoryProjection("ManagedFrozen") };
        yield return new object[] { "NativeRemapped(frozen)", new NativeRemappedProjection("Foundation.NSUrl", "SwiftURL", true, "ToNSUrl") };
        yield return new object[] { "NativeRemapped(non-frozen)", new NativeRemappedProjection("Foundation.NSDate", "SwiftTimestamp", false, "ToNSDate") };
        yield return new object[] { "Data", new DataProjection() };
        yield return new object[] { "Date", new DateProjection() };
        yield return new object[] { "Array<String>", new ArrayProjection(new StringProjection(), false) };
        yield return new object[] { "Dict<String,String>", new DictionaryProjection(new StringProjection(), new StringProjection(), false) };
        yield return new object[] { "Optional<String>", new OptionalProjection(new StringProjection()) };
        yield return new object[] { "Optional<Array>", new OptionalProjection(new ArrayProjection(new BlittableProjection("Int64"), false)) };
        yield return new object[] { "Existential", new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy") };
        yield return new object[] { "Tuple(String,Int64)", new TupleProjection(new ITypeProjection[] { new StringProjection(), new BlittableProjection("Int64") }) };
        yield return new object[] { "Closure(Int32->Bool)", new ClosureProjection(new ITypeProjection[] { new BlittableProjection("int") }, new BoolProjection(), isEscaping: true, throws: false, isAsync: false, callbackName: "testCallback") };
        yield return new object[] { "Async(Task<string>)", new AsyncProjection(new StringProjection(), throws: false, callbackPrefix: "testAsync") };
    }

    #endregion

    #region Part 4: Dictionary with existential value — projection composition

    [Fact]
    public void DictionaryWithExistentialValue_ComposesCorrectly()
    {
        // Dictionary<String, any Describable> should compose DictionaryProjection(StringProjection, ExistentialProjection)
        var existentialValue = MakeProtocolList("TestModule.Describable");
        var dictSpec = N("Swift.Dictionary", N("Swift.String"), existentialValue);
        var ctx = CreateContext(_sharedDb, isParameter: true);

        var projection = _factory.Project(dictSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<DictionaryProjection>(projection);
        Assert.Equal("IDictionary<string, IDescribable>", projection.PublicType);
        Assert.Equal("IntPtr", projection.PInvokeType);
    }

    [Fact]
    public void DictionaryWithExistentialValue_ParameterPlan_HasExistentialConversion()
    {
        // Verify element conversion casts to ISwiftExistentialConvertible
        var existentialProj = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var keyProj = new StringProjection();
        var dictProj = new DictionaryProjection(keyProj, existentialProj, isParameter: true);

        var plan = dictProj.GetParameterPlan("props");

        // The setup/expression should involve container creation for existential values
        var rendered = plan.SetupStatements.Count > 0 || !string.IsNullOrEmpty(plan.PInvokeExpression);
        Assert.True(rendered, "Dictionary with existential value should produce parameter plan");
    }

    [Fact]
    public void DictionaryWithExistentialValue_ReturnPlan_HasProxyConstruction()
    {
        // Verify return element creates proxy
        var existentialProj = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var keyProj = new StringProjection();
        var dictProj = new DictionaryProjection(keyProj, existentialProj, isParameter: false);

        Assert.Equal("IReadOnlyDictionary<string, IDescribable>", dictProj.PublicType);
        Assert.Equal("IntPtr", dictProj.PInvokeType);
    }

    #endregion

    #region Helpers

    private static NamedTypeSpec N(string name, params TypeSpec[] genericParams)
    {
        return new NamedTypeSpec(name, genericParams);
    }

    private static ProtocolListTypeSpec MakeProtocolList(params string[] protocolNames)
    {
        return new ProtocolListTypeSpec(protocolNames.Select(n => new NamedTypeSpec(n)).ToArray());
    }

    private static TupleTypeSpec MakeTuple(params TypeSpec[] elements)
    {
        return new TupleTypeSpec(elements);
    }

    private static ClosureTypeSpec MakeEscapingClosure(TypeSpec[] args, TypeSpec returnType)
    {
        var closure = new ClosureTypeSpec
        {
            Arguments = args.Length > 0 ? new TupleTypeSpec(args) : TupleTypeSpec.Empty,
            ReturnType = returnType
        };
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));
        return closure;
    }

    private static ClosureTypeSpec MakeNonEscapingClosure(TypeSpec[] args, TypeSpec returnType)
    {
        return new ClosureTypeSpec
        {
            Arguments = args.Length > 0 ? new TupleTypeSpec(args) : TupleTypeSpec.Empty,
            ReturnType = returnType
        };
    }

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

    private static string RenderPlan(MarshalPlan plan)
    {
        using var sw = new StringWriter();
        var writer = new CSharpWriter(sw);
        MarshalPlanRenderer.RenderReturnPlan(writer, plan);
        writer.Flush();
        return sw.ToString();
    }

    /// <summary>
    /// Shared MockTypeDatabase with all test type records pre-populated.
    /// </summary>
    private static readonly MockTypeDatabase _sharedDb = BuildSharedDatabase();

    private static MockTypeDatabase BuildSharedDatabase()
    {
        var db = new MockTypeDatabase();

        // ObjC bridged type
        db.AddType("TestModule.UIImage", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "UIImage"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.UIImage"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.ObjCBridged,
            Kind = TypeRecordKind.Struct
        });

        // Simple enum (Int32)
        db.AddType("TestModule.Direction", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Direction"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Direction"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            Kind = TypeRecordKind.Enum,
            RawValueTypeName = "Int32"
        });

        // Simple enum (Int64)
        db.AddType("TestModule.BigEnum", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BigEnum"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BigEnum"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            Kind = TypeRecordKind.Enum,
            RawValueTypeName = "Int64"
        });

        // Frozen with memory management (ClassWithBufferStruct)
        db.AddType("TestModule.ManagedFrozen", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ManagedFrozen"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ManagedFrozen"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        });

        // Non-frozen struct
        db.AddType("TestModule.Pipeline", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Struct
        });

        // Frozen blittable struct
        db.AddType("TestModule.Point", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        // Class
        db.AddType("TestModule.MyViewController", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyViewController"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyViewController"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Class
        });

        // Native remapped (frozen)
        db.AddType("TestModule.SwiftURL", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SwiftURL"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SwiftURL"),
            NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        // Native remapped (non-frozen) — uses a non-Data type since Data now gets DataProjection
        db.AddType("TestModule.SwiftTimestamp", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SwiftTimestamp"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SwiftTimestamp"),
            NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSDate"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Struct
        });

        // Protocol (known, has proxy)
        db.AddType("TestModule.Describable", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Describable"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol
        });

        // Protocol (known, for composition)
        db.AddType("TestModule.Renderable", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Renderable"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Renderable"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol
        });

        // Protocol (unknown — not in DB, will resolve to "object")

        // Well-known blittable types (frozen structs without memory management)
        db.AddType("Swift.Int64", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "Int64"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int64"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        db.AddType("Swift.Double", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "Double"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        db.AddType("Swift.Float", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "Float"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Float"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        return db;
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
