// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for TypeProjectionFactory — verifies correct routing of TypeSpec
/// to the appropriate ITypeProjection, and null for unsupported types.
/// </summary>
public class TypeProjectionFactoryTests
{
    private readonly TypeProjectionFactory _factory = new();

    #region Well-Known Simple Types

    [Fact]
    public void Project_SwiftBool_ReturnsBoolProjection()
    {
        var typeSpec = new NamedTypeSpec("Swift.Bool");
        var ctx = CreateContext();

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<BoolProjection>(projection);
        Assert.Equal("bool", projection.PublicType);
        Assert.Equal("[MarshalAs(UnmanagedType.U1)]", projection.PInvokeAttribute);
    }

    [Fact]
    public void Project_SwiftString_ReturnsStringProjection()
    {
        var typeSpec = new NamedTypeSpec("Swift.String");
        var ctx = CreateContext();

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<StringProjection>(projection);
        Assert.Equal("string", projection.PublicType);
        Assert.Equal("SwiftString", projection.PInvokeType);
    }

    #endregion

    #region TypeDatabase-Resolved Types

    [Fact]
    public void Project_ObjCBridgedType_ReturnsObjCBridgedProjection()
    {
        var db = new MockTypeDatabase();
        db.AddType("TestModule.BridgedType", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BridgedType"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BridgedType"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.ObjCBridged,
            Kind = TypeRecordKind.Class
        });
        var ctx = CreateContext(db);
        var typeSpec = new NamedTypeSpec("TestModule.BridgedType");

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<ObjCBridgedProjection>(projection);
        Assert.Equal("TestModule.BridgedType", projection.PublicType);
    }

    [Fact]
    public void Project_ObjCBridgeableType_ReturnsObjCBridgeableProjection()
    {
        var db = new MockTypeDatabase();
        db.AddType("Foundation.URL", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftURL"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridgeable,
            Kind = TypeRecordKind.Struct,
            NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl")
        });
        var ctx = CreateContext(db);
        var typeSpec = new NamedTypeSpec("Foundation.URL");

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<ObjCBridgeableProjection>(projection);
        Assert.Equal("Foundation.NSUrl", projection.PublicType);
        Assert.Equal("IntPtr", projection.PInvokeType);
    }

    [Fact]
    public void Project_ObjCBridgeable_TakesPriorityOverNativeRemapped()
    {
        // ObjCBridgeable flag should win over NativeTypeName → NativeRemappedProjection dispatch
        var db = new MockTypeDatabase();
        db.AddType("Foundation.URL", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftURL"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridgeable,
            Kind = TypeRecordKind.Struct,
            NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl")
        });
        var ctx = CreateContext(db);
        var typeSpec = new NamedTypeSpec("Foundation.URL");

        var projection = _factory.Project(typeSpec, ctx);

        // Must NOT be NativeRemappedProjection (SafeHandle) — must be ObjCBridgeableProjection (IntPtr)
        Assert.IsNotType<NativeRemappedProjection>(projection);
        Assert.IsType<ObjCBridgeableProjection>(projection);
    }

    [Fact]
    public void Project_SimpleEnum_ReturnsSimpleEnumProjection()
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
        Assert.Equal("TestModule.Direction", projection.PublicType);
        Assert.Equal("int", projection.PInvokeType);
    }

    [Fact]
    public void Project_FoundationData_ReturnsDataProjection()
    {
        // Foundation.Data → DataProjection with byte[] public type
        // Factory short-circuits before type database lookup
        var db = new MockTypeDatabase();
        var ctx = CreateContext(db);
        var typeSpec = new NamedTypeSpec("Foundation.Data");

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<DataProjection>(projection);
        Assert.Equal("byte[]", projection.PublicType);
        Assert.Equal("Swift.Data", projection.PInvokeType);
    }

    [Fact]
    public void Project_FoundationDate_ReturnsDateProjection()
    {
        // Foundation.Date → DateProjection with DateTimeOffset public type, double P/Invoke type
        // Factory short-circuits before type database lookup
        var db = new MockTypeDatabase();
        var ctx = CreateContext(db);
        var typeSpec = new NamedTypeSpec("Foundation.Date");

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<DateProjection>(projection);
        Assert.Equal("System.DateTimeOffset", projection.PublicType);
        Assert.Equal("double", projection.PInvokeType);
    }

    [Fact]
    public void Project_NativeRemappedNonFrozen_ReturnsNativeRemappedProjection()
    {
        // Foundation.URL → non-frozen, NativeTypeName = NSUrl, CSharpTypeName = SwiftURL
        var db = new MockTypeDatabase();
        db.AddType("Foundation.URL", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftURL"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct,
            NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl")
        });
        var ctx = CreateContext(db);
        var typeSpec = new NamedTypeSpec("Foundation.URL");

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<NativeRemappedProjection>(projection);
        Assert.Equal("Foundation.NSUrl", projection.PublicType);
        Assert.Equal("SafeHandle", projection.PInvokeType);
    }

    [Fact]
    public void Project_NonFrozenStruct_ReturnsNonFrozenStructProjection()
    {
        var db = new MockTypeDatabase();
        db.AddType("TestModule.NonFrozen", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "NonFrozen"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.NonFrozen"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Struct
        });
        var ctx = CreateContext(db);
        var typeSpec = new NamedTypeSpec("TestModule.NonFrozen");

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<NonFrozenStructProjection>(projection);
        Assert.Equal("TestModule.NonFrozen", projection.PublicType);
        Assert.Equal("IntPtr", projection.PInvokeType);
    }

    [Fact]
    public void Project_FrozenBlittableStruct_ReturnsBlittableProjection()
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
        var ctx = CreateContext(db);
        var typeSpec = new NamedTypeSpec("TestModule.Point");

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<BlittableProjection>(projection);
        Assert.Equal("TestModule.Point", projection.PublicType);
        Assert.Equal("TestModule.Point", projection.PInvokeType);
    }

    [Fact]
    public void Project_Class_ReturnsClassProjection()
    {
        var db = new MockTypeDatabase();
        db.AddType("TestModule.MyClass", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class
        });
        var ctx = CreateContext(db);
        var typeSpec = new NamedTypeSpec("TestModule.MyClass");

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<ClassProjection>(projection);
        Assert.Equal("TestModule.MyClass", projection.PublicType);
        Assert.Equal("IntPtr", projection.PInvokeType);
    }

    #endregion

    #region ClassProjection

    [Fact]
    public void ClassProjection_ParameterPlan_UsesDangerousGetHandle()
    {
        var projection = new ClassProjection("TestModule.MyClass");
        var plan = projection.GetParameterPlan("myParam");

        Assert.Equal("myParam.Payload.DangerousGetHandle()", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
        Assert.Empty(plan.CleanupStatements);
    }

    [Fact]
    public void ClassProjection_ReturnPlan_EmitsDirectMarshalFromSwift()
    {
        var projection = new ClassProjection("TestModule.MyClass");
        var plan = projection.GetReturnPlan("result", ReturnStrategy.Direct);

        // ARC bridge: no buffer allocation, direct MarshalFromSwift
        Assert.False(plan.RequiresUnsafe);
        Assert.Equal("(TestModule.MyClass)SwiftMarshal.MarshalFromSwift<TestModule.MyClass>(result)", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    [Fact]
    public void ClassProjection_ElementConversions()
    {
        var projection = new ClassProjection("TestModule.MyClass");

        Assert.Equal("e.Payload.DangerousGetHandle()", projection.GetParameterElementConversion("e"));
        // Return element conversion is null — when used inside Optional, ToNullable() handles
        // construction via ISwiftObject.NewFromPayload. Standalone returns use GetReturnPlan.
        Assert.Null(projection.GetReturnElementConversion("e"));
    }

    #endregion

    #region ObjCBridgeableProjection

    [Fact]
    public void ObjCBridgeableProjection_ParameterPlan_ExtractsHandle()
    {
        var projection = new ObjCBridgeableProjection("Foundation.NSUrl");
        var plan = projection.GetParameterPlan("url");

        Assert.Equal("urlHandle", plan.PInvokeExpression);
        Assert.Single(plan.SetupStatements);
        var line = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Equal("var urlHandle = url.Handle;", line.Code);
    }

    [Fact]
    public void ObjCBridgeableProjection_ReturnPlan_UsesObjCBridgeCall()
    {
        var projection = new ObjCBridgeableProjection("Foundation.NSUrl");
        var plan = projection.GetReturnPlan("result", ReturnStrategy.Direct);

        // Should use FormatObjCBridgeCall — wraps IntPtr via GetNSObject/GetINativeObject
        Assert.Contains("result", plan.PInvokeExpression);
        Assert.Contains("NSUrl", plan.PInvokeExpression);
    }

    [Fact]
    public void ObjCBridgeableProjection_Properties()
    {
        var projection = new ObjCBridgeableProjection("Foundation.NSUrl");

        Assert.Equal("Foundation.NSUrl", projection.PublicType);
        Assert.Equal("IntPtr", projection.PInvokeType);
        Assert.Null(projection.PInvokeAttribute);
        Assert.True(projection.UsesObjCContainerBridge);
        Assert.False(projection.RequiresSwiftWrapper);
        Assert.False(projection.ElementRequiresDisposal);
    }

    [Fact]
    public void ObjCBridgeableProjection_ElementConversions()
    {
        var projection = new ObjCBridgeableProjection("Foundation.NSUrl");

        // Parameter element: extract Handle for container bridging
        Assert.Equal("e.Handle", projection.GetParameterElementConversion("e"));

        // Return element: wrap IntPtr via ObjC bridge call
        var returnConv = projection.GetReturnElementConversion("e");
        Assert.NotNull(returnConv);
        Assert.Contains("NSUrl", returnConv!);
    }

    [Fact]
    public void ObjCBridgeableProjection_AcceptDispatchesToCorrectVisitOverload()
    {
        var projection = new ObjCBridgeableProjection("Foundation.NSUrl");
        // Verify Accept() calls Visit(ObjCBridgeableProjection) — compile-time exhaustive
        var visitor = new BridgeableVisitorProbe();
        var result = projection.Accept(visitor);
        Assert.True(result);
    }

    private class BridgeableVisitorProbe : IProjectionVisitor<bool>
    {
        public bool Visit(ObjCBridgeableProjection p) => true;
        // All other overloads return false
        public bool Visit(StringProjection p) => false;
        public bool Visit(BlittableProjection p) => false;
        public bool Visit(BoolProjection p) => false;
        public bool Visit(SimpleEnumProjection p) => false;
        public bool Visit(ClassProjection p) => false;
        public bool Visit(NonFrozenStructProjection p) => false;
        public bool Visit(FrozenWithMemoryProjection p) => false;
        public bool Visit(ArrayProjection p) => false;
        public bool Visit(DictionaryProjection p) => false;
        public bool Visit(SetProjection p) => false;
        public bool Visit(DataProjection p) => false;
        public bool Visit(OptionalProjection p) => false;
        public bool Visit(ExistentialProjection p) => false;
        public bool Visit(ClosureProjection p) => false;
        public bool Visit(AsyncProjection p) => false;
        public bool Visit(ObjCBridgedProjection p) => false;
        public bool Visit(ObjCRootedClassProjection p) => false;
        public bool Visit(NativeRemappedProjection p) => false;
        public bool Visit(TupleProjection p) => false;
        public bool Visit(DateProjection p) => false;
        public bool Visit(ResultProjection p) => false;
    }

    #endregion

    #region Unsupported Types Return Null

    [Fact]
    public void Project_TupleType_ReturnsNull()
    {
        var typeSpec = new TupleTypeSpec();
        var ctx = CreateContext();

        var projection = _factory.Project(typeSpec, ctx);

        Assert.Null(projection);
    }

    [Fact]
    public void Project_ClosureType_ReturnsClosureProjection()
    {
        var typeSpec = new ClosureTypeSpec();
        var ctx = CreateContext();

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<ClosureProjection>(projection);
        Assert.Equal("Action", projection.PublicType);
    }

    [Fact]
    public void Project_UnknownNamedType_ReturnsNull()
    {
        var typeSpec = new NamedTypeSpec("Unknown.Type");
        var ctx = CreateContext();

        var projection = _factory.Project(typeSpec, ctx);

        Assert.Null(projection);
    }

    [Fact]
    public void Project_FrozenWithMemoryManagement_ReturnsFrozenWithMemoryProjection()
    {
        // Frozen + RequiresMemoryManagement (ClassWithBufferStruct) — P/Invoke returns .Buffer
        var db = new MockTypeDatabase();
        db.AddType("TestModule.ManagedFrozen", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ManagedFrozen"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ManagedFrozen"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        });
        var ctx = CreateContext(db);
        var typeSpec = new NamedTypeSpec("TestModule.ManagedFrozen");

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<FrozenWithMemoryProjection>(projection);
        Assert.Equal("TestModule.ManagedFrozen", projection.PublicType);
        Assert.Equal("TestModule.ManagedFrozen.Buffer", projection.PInvokeType);
    }

    #endregion

    #region FrozenWithMemoryProjection

    [Fact]
    public void FrozenWithMemoryProjection_ParameterPlan_UsesPayloadBuffer()
    {
        var projection = new FrozenWithMemoryProjection("TestModule.ManagedFrozen");
        var plan = projection.GetParameterPlan("item");

        Assert.Equal("itemDisposable.Buffer", plan.PInvokeExpression);
        Assert.Single(plan.SetupStatements);
        var usingStmt = Assert.IsType<MarshalStatement.Using>(plan.SetupStatements[0]);
        Assert.Equal("PayloadBuffer<TestModule.ManagedFrozen.Buffer>", usingStmt.Type);
        Assert.Equal("itemDisposable", usingStmt.Name);
        Assert.Equal("item.PayloadBuffer", usingStmt.InitExpression);
    }

    [Fact]
    public void FrozenWithMemoryProjection_ReturnPlan_Direct_MarshalFromSwiftWithAddressOf()
    {
        var projection = new FrozenWithMemoryProjection("TestModule.ManagedFrozen");
        var plan = projection.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.True(plan.RequiresUnsafe);
        Assert.Equal("SwiftMarshal.MarshalFromSwift<TestModule.ManagedFrozen>(new IntPtr(&result))", plan.PInvokeExpression);
    }

    [Fact]
    public void FrozenWithMemoryProjection_ReturnPlan_IndirectResult_MarshalFromSwiftDirect()
    {
        var projection = new FrozenWithMemoryProjection("TestModule.ManagedFrozen");
        var plan = projection.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.False(plan.RequiresUnsafe);
        Assert.Equal("SwiftMarshal.MarshalFromSwift<TestModule.ManagedFrozen>(result)", plan.PInvokeExpression);
    }

    [Fact]
    public void FrozenWithMemoryProjection_ReturnPlan_OutBuffer_MarshalFromSwiftDirect()
    {
        var projection = new FrozenWithMemoryProjection("TestModule.ManagedFrozen");
        var plan = projection.GetReturnPlan("_optRetPtr", ReturnStrategy.OutBuffer);

        Assert.Equal("SwiftMarshal.MarshalFromSwift<TestModule.ManagedFrozen>(_optRetPtr)", plan.PInvokeExpression);
    }

    [Fact]
    public void FrozenWithMemoryProjection_ContainerTypeName_UsesBuffer()
    {
        var projection = new FrozenWithMemoryProjection("TestModule.ManagedFrozen");
        Assert.Equal("TestModule.ManagedFrozen.Buffer", projection.ContainerTypeName);
        Assert.Equal("TestModule.ManagedFrozen.Buffer", projection.SwiftContainerGenericType);
    }

    [Fact]
    public void FrozenWithMemoryProjection_MarshalFromSwiftType_UsesTypeName()
    {
        var projection = new FrozenWithMemoryProjection("TestModule.ManagedFrozen");
        Assert.Equal("TestModule.ManagedFrozen", projection.MarshalFromSwiftType);
    }

    [Fact]
    public void FrozenWithMemoryProjection_ElementConversions()
    {
        var projection = new FrozenWithMemoryProjection("TestModule.ManagedFrozen");

        // Parameter element conversion returns null — frozen-with-memory types can't be safely
        // composed inside containers (PayloadBuffer lifecycle can't be managed in a LINQ Select).
        // Returning null causes a C# compile error if this composition is ever attempted.
        Assert.Null(projection.GetParameterElementConversion("e"));
        Assert.Null(projection.GetReturnElementConversion("e"));
    }

    [Fact]
    public void OptionalProjection_FrozenWithMemory_ContainerTypeName()
    {
        // ContainerTypeName uses MarshalFromSwiftType (public type name) for MarshalFromSwift calls.
        // SwiftContainerGenericType uses SwiftContainerGenericType (.Buffer) for P/Invoke generic params.
        var inner = new FrozenWithMemoryProjection("TestModule.ManagedFrozen");
        var optional = new OptionalProjection(inner);

        Assert.Equal("SwiftOptional<TestModule.ManagedFrozen>", optional.ContainerTypeName);
        Assert.Equal("SwiftOptional<TestModule.ManagedFrozen.Buffer>", optional.SwiftContainerGenericType);
    }

    #endregion

    #region ResultProjection

    [Fact]
    public void Project_SwiftResult_ReturnsResultProjection()
    {
        var typeSpec = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("Swift.String"), new NamedTypeSpec("Swift.Bool"));
        var ctx = CreateContext();

        var projection = _factory.Project(typeSpec, ctx);

        Assert.NotNull(projection);
        Assert.IsType<ResultProjection>(projection);
    }

    [Fact]
    public void ResultProjection_PublicType_ContainsBothTypes()
    {
        var success = new BlittableProjection("int");
        var failure = new BlittableProjection("nint");
        var proj = new ResultProjection(success, failure);

        Assert.Contains("SwiftResult<", proj.PublicType);
        Assert.Contains("int", proj.PublicType);
        Assert.Contains("nint", proj.PublicType);
    }

    [Fact]
    public void ResultProjection_PInvokeType_IsIntPtr()
    {
        var success = new BlittableProjection("int");
        var failure = new BlittableProjection("nint");
        var proj = new ResultProjection(success, failure);

        Assert.Equal("IntPtr", proj.PInvokeType);
        Assert.Null(proj.PInvokeAttribute);
    }

    [Fact]
    public void ResultProjection_ReturnPlan_UsesMarshalFromSwift()
    {
        var success = new StringProjection();
        var failure = new BlittableProjection("int");
        var proj = new ResultProjection(success, failure);

        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);
        Assert.Contains("MarshalFromSwift<SwiftResult<", plan.SetupStatements[0].ToString());
    }

    [Fact]
    public void ResultProjection_ParameterPlan_ThrowsNotSupported()
    {
        var success = new BlittableProjection("int");
        var failure = new BlittableProjection("nint");
        var proj = new ResultProjection(success, failure);

        Assert.Throws<NotSupportedException>(() => proj.GetParameterPlan("myResult"));
    }

    [Fact]
    public void ResultProjection_DoesNotRequireSwiftWrapper()
    {
        var success = new BlittableProjection("int");
        var failure = new BlittableProjection("nint");
        var proj = new ResultProjection(success, failure);

        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void ResultProjection_Accept_VisitsResultProjection()
    {
        var success = new BlittableProjection("int");
        var failure = new BlittableProjection("nint");
        var proj = new ResultProjection(success, failure);
        var visitor = new ProjectionVisitorTests.TypeNameCollectorVisitor();
        var result = proj.Accept(visitor);
        Assert.Equal("ResultProjection", result);
    }

    [Fact]
    public void Project_SwiftResult_NullWhenInnerFails()
    {
        // Result with unresolvable inner type → null
        var typeSpec = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("Unknown.Type"), new NamedTypeSpec("Swift.Bool"));
        var ctx = CreateContext();

        var projection = _factory.Project(typeSpec, ctx);
        Assert.Null(projection);
    }

    [Fact]
    public void ResultProjection_ContainerTypeName_UsesInnerMarshalTypes()
    {
        var success = new ClassProjection("MyApp.FetchData");
        var failure = new NonFrozenStructProjection("MyApp.FetchError");
        var proj = new ResultProjection(success, failure);

        Assert.Contains("SwiftResult<MyApp.FetchData, MyApp.FetchError>", proj.ContainerTypeName);
    }

    #endregion

    #region Helpers

    private static ProjectionContext CreateContext(ITypeDatabase? db = null)
    {
        return new ProjectionContext
        {
            TypeDatabase = db ?? new MockTypeDatabase()
        };
    }

    /// <summary>
    /// Minimal ITypeDatabase for factory tests.
    /// </summary>
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
