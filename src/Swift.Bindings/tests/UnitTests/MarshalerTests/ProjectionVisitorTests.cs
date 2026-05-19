// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for projection types that lacked dedicated unit tests.
/// Covers Visit() dispatch, PublicType/PInvokeType, parameter plans, and return plans
/// for ClassProjection, FrozenWithMemoryProjection, ObjCRootedClassProjection,
/// OptionalProjection, SetProjection, DictionaryProjection, TupleProjection, AsyncProjection,
/// ArrayProjection, ExistentialProjection, and ClosureProjection.
/// </summary>
public class ProjectionVisitorTests
{
    #region ClassProjection

    [Fact]
    public void Class_Types()
    {
        var proj = new ClassProjection("Loader");
        Assert.Equal("Loader", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void Class_PInvokeAttribute_IsNull()
    {
        var proj = new ClassProjection("Loader");
        Assert.Null(proj.PInvokeAttribute);
    }

    [Fact]
    public void Class_ParameterPlan_ExtractsPayload()
    {
        var proj = new ClassProjection("Loader");
        var plan = proj.GetParameterPlan("loader");
        Assert.Equal("loader.Payload.DangerousGetHandle()", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    [Fact]
    public void Class_ReturnPlan_UsesMarshalFromSwift()
    {
        var proj = new ClassProjection("Loader");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Contains("MarshalFromSwiftObject<Loader>", plan.PInvokeExpression);
    }

    [Fact]
    public void Class_ReturnPlan_IndirectResult()
    {
        var proj = new ClassProjection("Loader");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);
        Assert.Contains("MarshalFromSwiftObject<Loader>", plan.PInvokeExpression);
    }

    [Fact]
    public void Class_DoesNotRequireSwiftWrapper()
    {
        var proj = new ClassProjection("Loader");
        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void Class_MarshalFromSwiftType_IsClassName()
    {
        var proj = new ClassProjection("Loader");
        Assert.Equal("Loader", proj.MarshalFromSwiftType);
    }

    [Fact]
    public void Class_Accept_VisitsClassProjection()
    {
        var proj = new ClassProjection("Loader");
        var visitor = new TypeNameCollectorVisitor();
        var result = proj.Accept(visitor);
        Assert.Equal("ClassProjection", result);
    }

    #endregion

    #region FrozenWithMemoryProjection

    [Fact]
    public void FrozenWithMemory_Types()
    {
        var proj = new FrozenWithMemoryProjection("Config");
        Assert.Equal("Config", proj.PublicType);
        Assert.Equal("Config.Buffer", proj.PInvokeType);
    }

    [Fact]
    public void FrozenWithMemory_ParameterPlan_ExtractsBuffer()
    {
        var proj = new FrozenWithMemoryProjection("Config");
        var plan = proj.GetParameterPlan("cfg");
        Assert.Contains("Buffer", plan.PInvokeExpression);
    }

    [Fact]
    public void FrozenWithMemory_ReturnPlan_Direct()
    {
        var proj = new FrozenWithMemoryProjection("Config");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Contains("Config", plan.PInvokeExpression);
    }

    [Fact]
    public void FrozenWithMemory_DoesNotRequireSwiftWrapper()
    {
        var proj = new FrozenWithMemoryProjection("Config");
        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void FrozenWithMemory_Accept_VisitsFrozenWithMemoryProjection()
    {
        var proj = new FrozenWithMemoryProjection("Config");
        var visitor = new TypeNameCollectorVisitor();
        var result = proj.Accept(visitor);
        Assert.Equal("FrozenWithMemoryProjection", result);
    }

    #endregion

    #region ObjCRootedClassProjection

    [Fact]
    public void ObjCRootedClass_Types()
    {
        var proj = new ObjCRootedClassProjection("UIViewController");
        Assert.Equal("UIViewController", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void ObjCRootedClass_ParameterPlan_UsesStackallocBuffer()
    {
        var proj = new ObjCRootedClassProjection("UIViewController");
        var plan = proj.GetParameterPlan("vc");
        // ObjCRooted uses stackalloc temp buffer: PInvokeExpression is (IntPtr)_vc_ptr
        Assert.Contains("_ptr", plan.PInvokeExpression);
        Assert.NotEmpty(plan.SetupStatements);
    }

    [Fact]
    public void ObjCRootedClass_ReturnPlan_UsesMarshalFromSwift()
    {
        var proj = new ObjCRootedClassProjection("UIViewController");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Contains("MarshalFromSwiftObject", plan.PInvokeExpression);
    }

    [Fact]
    public void ObjCRootedClass_DoesNotRequireSwiftWrapper()
    {
        var proj = new ObjCRootedClassProjection("UIViewController");
        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void ObjCRootedClass_Accept_VisitsObjCRootedClassProjection()
    {
        var proj = new ObjCRootedClassProjection("UIViewController");
        var visitor = new TypeNameCollectorVisitor();
        var result = proj.Accept(visitor);
        Assert.Equal("ObjCRootedClassProjection", result);
    }

    #endregion

    #region OptionalProjection

    [Fact]
    public void Optional_WithBlittable_Types()
    {
        var inner = new BlittableProjection("nint");
        var proj = new OptionalProjection(inner);
        Assert.Equal("nint?", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void Optional_WithClass_Types()
    {
        var inner = new ClassProjection("Loader");
        var proj = new OptionalProjection(inner);
        Assert.Equal("Loader?", proj.PublicType);
    }

    [Fact]
    public void Optional_PInvokeAttribute_IsNull()
    {
        var inner = new BlittableProjection("nint");
        var proj = new OptionalProjection(inner);
        Assert.Null(proj.PInvokeAttribute);
    }

    [Fact]
    public void Optional_DoesNotRequireSwiftWrapper()
    {
        var inner = new BlittableProjection("nint");
        var proj = new OptionalProjection(inner);
        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void Optional_Accept_VisitsOptionalProjection()
    {
        var inner = new BlittableProjection("nint");
        var proj = new OptionalProjection(inner);
        var visitor = new TypeNameCollectorVisitor();
        var result = proj.Accept(visitor);
        Assert.Equal("OptionalProjection", result);
    }

    #endregion

    #region SetProjection

    [Fact]
    public void Set_AsParameter_PublicTypeIsReadOnlySet()
    {
        // Bundle 04 #9: Set parameters now project as IReadOnlySet<T> (was
        // IEnumerable<T> pre-fix, which dropped Swift's uniqueness invariant
        // at the public API surface). Callers must materialise an actual set
        // (HashSet<T>) on the C# side.
        var elemProj = new BlittableProjection("nint");
        var proj = new SetProjection(elemProj, isParameter: true);
        Assert.Contains("IReadOnlySet", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void Set_AsReturn_PublicTypeIsReadOnlySet()
    {
        var elemProj = new BlittableProjection("nint");
        var proj = new SetProjection(elemProj, isParameter: false);
        Assert.Contains("IReadOnlySet", proj.PublicType);
    }

    [Fact]
    public void Set_DoesNotRequireSwiftWrapper()
    {
        var elemProj = new BlittableProjection("nint");
        var proj = new SetProjection(elemProj, isParameter: true);
        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void Set_Accept_VisitsSetProjection()
    {
        var elemProj = new BlittableProjection("nint");
        var proj = new SetProjection(elemProj, isParameter: true);
        var visitor = new TypeNameCollectorVisitor();
        var result = proj.Accept(visitor);
        Assert.Equal("SetProjection", result);
    }

    #endregion

    #region DictionaryProjection

    [Fact]
    public void Dict_AsParameter_PublicTypeIsIDictionary()
    {
        var keyProj = new StringProjection();
        var valProj = new BlittableProjection("nint");
        var proj = new DictionaryProjection(keyProj, valProj, isParameter: true);
        Assert.Contains("IDictionary", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void Dict_AsReturn_PublicTypeIsReadOnlyDictionary()
    {
        var keyProj = new StringProjection();
        var valProj = new BlittableProjection("nint");
        var proj = new DictionaryProjection(keyProj, valProj, isParameter: false);
        Assert.Contains("IReadOnlyDictionary", proj.PublicType);
    }

    [Fact]
    public void Dict_DoesNotRequireSwiftWrapper()
    {
        var keyProj = new StringProjection();
        var valProj = new BlittableProjection("nint");
        var proj = new DictionaryProjection(keyProj, valProj, isParameter: true);
        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void Dict_Accept_VisitsDictionaryProjection()
    {
        var keyProj = new StringProjection();
        var valProj = new BlittableProjection("nint");
        var proj = new DictionaryProjection(keyProj, valProj, isParameter: true);
        var visitor = new TypeNameCollectorVisitor();
        var result = proj.Accept(visitor);
        Assert.Equal("DictionaryProjection", result);
    }

    #endregion

    #region TupleProjection

    [Fact]
    public void Tuple_TwoElements_Types()
    {
        var elems = new List<ITypeProjection>
        {
            new BlittableProjection("nint"),
            new BoolProjection()
        };
        var proj = new TupleProjection(elems);
        Assert.Contains("nint", proj.PublicType);
        Assert.Contains("bool", proj.PublicType);
        Assert.Contains("ValueTuple", proj.PInvokeType);
    }

    [Fact]
    public void Tuple_DoesNotRequireSwiftWrapper()
    {
        var elems = new List<ITypeProjection> { new BlittableProjection("nint") };
        var proj = new TupleProjection(elems);
        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void Tuple_Accept_VisitsTupleProjection()
    {
        var elems = new List<ITypeProjection> { new BlittableProjection("nint") };
        var proj = new TupleProjection(elems);
        var visitor = new TypeNameCollectorVisitor();
        var result = proj.Accept(visitor);
        Assert.Equal("TupleProjection", result);
    }

    #endregion

    #region AsyncProjection

    [Fact]
    public void Async_VoidReturn_PublicTypeIsTask()
    {
        var proj = new AsyncProjection(null, throws: false, callbackPrefix: null);
        Assert.Equal("global::System.Threading.Tasks.Task", proj.PublicType);
        Assert.Equal("void", proj.PInvokeType);
    }

    [Fact]
    public void Async_WithReturn_PublicTypeIsTaskOfT()
    {
        var inner = new BlittableProjection("nint");
        var proj = new AsyncProjection(inner, throws: false, callbackPrefix: null);
        Assert.Contains("Task<", proj.PublicType);
    }

    [Fact]
    public void Async_RequiresSwiftWrapper()
    {
        var proj = new AsyncProjection(null, throws: false, callbackPrefix: null);
        Assert.True(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void Async_Accept_VisitsAsyncProjection()
    {
        var proj = new AsyncProjection(null, throws: false, callbackPrefix: null);
        var visitor = new TypeNameCollectorVisitor();
        var result = proj.Accept(visitor);
        Assert.Equal("AsyncProjection", result);
    }

    #endregion

    #region ArrayProjection

    [Fact]
    public void Array_AsParameter_PublicTypeIsEnumerable()
    {
        var elemProj = new BlittableProjection("nint");
        var proj = new ArrayProjection(elemProj, isParameter: true);
        Assert.Contains("IEnumerable", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void Array_AsReturn_PublicTypeIsReadOnlyList()
    {
        var elemProj = new BlittableProjection("nint");
        var proj = new ArrayProjection(elemProj, isParameter: false);
        Assert.Contains("IReadOnlyList", proj.PublicType);
    }

    [Fact]
    public void Array_ContainerTypeName_IncludesElement()
    {
        var elemProj = new BlittableProjection("nint");
        var proj = new ArrayProjection(elemProj, isParameter: true);
        Assert.Contains("SwiftArray", proj.ContainerTypeName);
    }

    [Fact]
    public void Array_DoesNotRequireSwiftWrapper()
    {
        var elemProj = new BlittableProjection("nint");
        var proj = new ArrayProjection(elemProj, isParameter: true);
        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void Array_Accept_VisitsArrayProjection()
    {
        var elemProj = new BlittableProjection("nint");
        var proj = new ArrayProjection(elemProj, isParameter: true);
        var visitor = new TypeNameCollectorVisitor();
        var result = proj.Accept(visitor);
        Assert.Equal("ArrayProjection", result);
    }

    #endregion

    #region ExistentialProjection

    [Fact]
    public void Existential_WellKnown_PublicType()
    {
        var proj = new ExistentialProjection(
            containerType: "ExistentialContainer1",
            publicType: "AnyError",
            proxyClassName: null);
        Assert.Equal("AnyError", proj.PublicType);
        Assert.Equal("ExistentialContainer1", proj.PInvokeType);
    }

    [Fact]
    public void Existential_Proxy_Types()
    {
        var proj = new ExistentialProjection(
            containerType: "ExistentialContainer1",
            publicType: "IMyProtocol",
            proxyClassName: "MyProtocolProxy");
        Assert.Equal("IMyProtocol", proj.PublicType);
        Assert.Equal("ExistentialContainer1", proj.PInvokeType);
    }

    [Fact]
    public void Existential_UnknownProtocol_PublicTypeIsObject()
    {
        var proj = new ExistentialProjection(
            containerType: "ExistentialContainer0",
            publicType: "object",
            proxyClassName: null);
        Assert.Equal("object", proj.PublicType);
    }

    [Fact]
    public void Existential_DoesNotRequireSwiftWrapper()
    {
        var proj = new ExistentialProjection(
            containerType: "ExistentialContainer1",
            publicType: "AnyError",
            proxyClassName: null);
        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void Existential_Accept_VisitsExistentialProjection()
    {
        var proj = new ExistentialProjection(
            containerType: "ExistentialContainer1",
            publicType: "AnyError",
            proxyClassName: null);
        var visitor = new TypeNameCollectorVisitor();
        var result = proj.Accept(visitor);
        Assert.Equal("ExistentialProjection", result);
    }

    #endregion

    #region ClosureProjection — basic type checks

    [Fact]
    public void Closure_Action_PublicType()
    {
        var proj = new ClosureProjection(
            argProjections: new List<ITypeProjection>(),
            returnProjection: null,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "cb_doWork");
        Assert.Equal("global::System.Action", proj.PublicType);
    }

    [Fact]
    public void Closure_Escaping_PInvokeIsSwiftClosureData()
    {
        var proj = new ClosureProjection(
            argProjections: new List<ITypeProjection> { new BlittableProjection("nint") },
            returnProjection: null,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "cb_doWork");
        Assert.Equal("SwiftClosureData", proj.PInvokeType);
    }

    [Fact]
    public void Closure_Accept_VisitsClosureProjection()
    {
        var proj = new ClosureProjection(
            argProjections: new List<ITypeProjection>(),
            returnProjection: null,
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "cb_doWork");
        var visitor = new TypeNameCollectorVisitor();
        var result = proj.Accept(visitor);
        Assert.Equal("ClosureProjection", result);
    }

    #endregion

    #region Visitor dispatch verifies exhaustive coverage

    /// <summary>
    /// Visitor that returns the projection type name — verifies Accept() dispatches correctly.
    /// </summary>
    internal class TypeNameCollectorVisitor : IProjectionVisitor<string>
    {
        public string Visit(StringProjection p) => "StringProjection";
        public string Visit(BlittableProjection p) => "BlittableProjection";
        public string Visit(BoolProjection p) => "BoolProjection";
        public string Visit(SimpleEnumProjection p) => "SimpleEnumProjection";
        public string Visit(ClassProjection p) => "ClassProjection";
        public string Visit(NonFrozenStructProjection p) => "NonFrozenStructProjection";
        public string Visit(FrozenWithMemoryProjection p) => "FrozenWithMemoryProjection";
        public string Visit(ArrayProjection p) => "ArrayProjection";
        public string Visit(DictionaryProjection p) => "DictionaryProjection";
        public string Visit(SetProjection p) => "SetProjection";
        public string Visit(DataProjection p) => "DataProjection";
        public string Visit(OptionalProjection p) => "OptionalProjection";
        public string Visit(ExistentialProjection p) => "ExistentialProjection";
        public string Visit(ClosureProjection p) => "ClosureProjection";
        public string Visit(AsyncProjection p) => "AsyncProjection";
        public string Visit(ObjCBridgedProjection p) => "ObjCBridgedProjection";
        public string Visit(ObjCBridgeableProjection p) => "ObjCBridgeableProjection";
        public string Visit(ObjCRootedClassProjection p) => "ObjCRootedClassProjection";
        public string Visit(NativeRemappedProjection p) => "NativeRemappedProjection";
        public string Visit(TupleProjection p) => "TupleProjection";
        public string Visit(DateProjection p) => "DateProjection";
        public string Visit(ResultProjection p) => "ResultProjection";
        public string Visit(KeyPathProjection p) => "KeyPathProjection";
    }

    #endregion
}
