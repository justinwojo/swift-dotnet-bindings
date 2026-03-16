// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regression tests for MarshalPlan rendering. Each test captures the exact C# code
/// produced by GetParameterPlan/GetReturnPlan to prevent unintentional changes.
///
/// Tests marked [Trait("Stability", "PreSession5")] capture current behavior that is
/// known to diverge from WrapperEmitter emission. Session 5 will unify these paths.
/// </summary>
public class MarshalPlanRegressionTests
{
    #region Blittable

    [Fact]
    public void Blittable_ParameterPlan_PassThrough()
    {
        var proj = new BlittableProjection("Int64");
        var plan = proj.GetParameterPlan("count");

        Assert.Equal("count", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
        Assert.Empty(plan.CleanupStatements);
    }

    [Fact]
    public void Blittable_ReturnPlan_PassThrough()
    {
        var proj = new BlittableProjection("Int64");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Equal("result", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    #endregion

    #region Bool

    [Fact]
    public void Bool_ParameterPlan_PassThrough()
    {
        var proj = new BoolProjection();
        var plan = proj.GetParameterPlan("flag");

        Assert.Equal("flag", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    [Fact]
    public void Bool_ReturnPlan_PassThrough()
    {
        var proj = new BoolProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Equal("result", plan.PInvokeExpression);
    }

    [Fact]
    public void Bool_HasMarshalAsAttribute()
    {
        var proj = new BoolProjection();
        Assert.Equal("[MarshalAs(UnmanagedType.U1)]", proj.PInvokeAttribute);
    }

    #endregion

    #region String

    [Fact]
    public void String_ParameterPlan_CreatesSwiftStringUsing()
    {
        var proj = new StringProjection();
        var plan = proj.GetParameterPlan("name");

        Assert.Equal("nameDisposable.Buffer", plan.PInvokeExpression);

        var usingStmt = Assert.IsType<MarshalStatement.Using>(plan.SetupStatements[0]);
        Assert.Equal("SwiftString", usingStmt.Type);
        Assert.Equal("nameSwift", usingStmt.Name);
        Assert.Equal("new SwiftString(name)", usingStmt.InitExpression);

        var payloadUsing = Assert.IsType<MarshalStatement.Using>(plan.SetupStatements[1]);
        Assert.Equal("PayloadBuffer<SwiftString.Buffer>", payloadUsing.Type);
        Assert.Equal("nameDisposable", payloadUsing.Name);
        Assert.Equal("nameSwift.PayloadBuffer", payloadUsing.InitExpression);
    }

    [Fact]
    public void String_ReturnPlan_Direct_MarshalFromSwiftThenToString()
    {
        var proj = new StringProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Equal("swiftResult.ToString()", plan.PInvokeExpression);
        Assert.True(plan.RequiresUnsafe);
        Assert.Single(plan.SetupStatements);
        var setupLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("SwiftMarshal.MarshalFromSwift<SwiftString>(new IntPtr(&result))", setupLine.Code);
    }

    [Fact]
    public void String_ReturnPlan_IndirectResult_MarshalFromSwift()
    {
        var proj = new StringProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Equal("SwiftString.MarshalFromSwift(result)", plan.PInvokeExpression);
    }

    [Fact]
    public void String_ReturnPlan_OutBuffer_PassThrough()
    {
        var proj = new StringProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.OutBuffer);

        Assert.Equal("result", plan.PInvokeExpression);
    }

    #endregion

    #region SimpleEnum

    [Fact]
    public void SimpleEnum_ParameterPlan_CastsToUnderlying()
    {
        var proj = new SimpleEnumProjection("Direction", "int");
        var plan = proj.GetParameterPlan("dir");

        Assert.Equal("(int)dir", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    [Fact]
    public void SimpleEnum_ReturnPlan_CastsFromUnderlying()
    {
        var proj = new SimpleEnumProjection("Direction", "int");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Equal("(Direction)result", plan.PInvokeExpression);
    }

    #endregion

    #region ObjCBridged

    [Fact]
    public void ObjCBridged_ParameterPlan_ExtractsHandle()
    {
        var proj = new ObjCBridgedProjection("UIImage");
        var plan = proj.GetParameterPlan("image");

        var setupLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("imageHandle", setupLine.Code);
        Assert.Contains("image.Handle", setupLine.Code);
        Assert.Equal("imageHandle", plan.PInvokeExpression);
    }

    [Fact]
    public void ObjCBridged_ReturnPlan_GetNSObject()
    {
        var proj = new ObjCBridgedProjection("UIImage");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Contains("GetNSObject<UIImage>(result)", plan.PInvokeExpression);
    }

    #endregion

    #region NativeRemapped

    [Fact]
    public void NativeRemapped_Frozen_ParameterPlan_VarNotUsing()
    {
        var proj = new NativeRemappedProjection("Foundation.NSUrl", "SwiftURL", isFrozen: true, toConversionMethod: "ToNSUrl");
        var plan = proj.GetParameterPlan("url");

        // Frozen types use var (no using) — no disposal needed for value types
        var lineStmt = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("new SwiftURL(url)", lineStmt.Code);
        Assert.Contains("urlSwift", lineStmt.Code);
        Assert.Equal("urlSwift", plan.PInvokeExpression);
    }

    [Fact]
    public void NativeRemapped_NonFrozen_ParameterPlan_UsesPayload()
    {
        var proj = new NativeRemappedProjection("Foundation.NSDate", "SwiftTimestamp", isFrozen: false, toConversionMethod: "ToNSDate");
        var plan = proj.GetParameterPlan("data");

        Assert.Contains("Payload", plan.PInvokeExpression);
    }

    [Fact]
    public void NativeRemapped_ReturnPlan_ConvertsToPublicType()
    {
        // Factory passes toConversionMethod from the short name (e.g., "ToNSUrl")
        var proj = new NativeRemappedProjection("Foundation.NSUrl", "SwiftURL", isFrozen: true,
            toConversionMethod: "ToNSUrl");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Contains("result.ToNSUrl()", plan.PInvokeExpression);
    }

    [Fact]
    public void NativeRemapped_NonFrozen_ReturnPlan_ConvertsToPublicType()
    {
        // Factory passes toConversionMethod from the short name (e.g., "ToNSDate")
        var proj = new NativeRemappedProjection("Foundation.NSDate", "SwiftTimestamp", isFrozen: false,
            toConversionMethod: "ToNSDate");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Contains("new SwiftTimestamp(result).ToNSDate()", plan.PInvokeExpression);
    }

    #endregion

    #region NonFrozenStruct

    [Fact]
    public void NonFrozenStruct_ParameterPlan_DangerousGetHandle()
    {
        var proj = new NonFrozenStructProjection("Pipeline");
        var plan = proj.GetParameterPlan("pipe");

        Assert.Equal("pipe.Payload.DangerousGetHandle()", plan.PInvokeExpression);
    }

    [Fact]
    public void NonFrozenStruct_ReturnPlan_UsesMarshalFromSwift()
    {
        var proj = new NonFrozenStructProjection("Pipeline");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Equal("SwiftMarshal.MarshalFromSwift<Pipeline>(result)", plan.PInvokeExpression);
    }

    #endregion

    #region FrozenWithMemory

    [Fact]
    public void FrozenWithMemory_ParameterPlan_PayloadBuffer()
    {
        var proj = new FrozenWithMemoryProjection("ManagedFrozen");
        var plan = proj.GetParameterPlan("item");

        Assert.Equal("itemDisposable.Buffer", plan.PInvokeExpression);
        Assert.Single(plan.SetupStatements);
        var usingStmt = Assert.IsType<MarshalStatement.Using>(plan.SetupStatements[0]);
        Assert.Equal("PayloadBuffer<ManagedFrozen.Buffer>", usingStmt.Type);
        Assert.Equal("item.PayloadBuffer", usingStmt.InitExpression);
    }

    [Fact]
    public void FrozenWithMemory_ReturnPlan_Direct_RequiresUnsafe()
    {
        var proj = new FrozenWithMemoryProjection("ManagedFrozen");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.True(plan.RequiresUnsafe);
        Assert.Equal("SwiftMarshal.MarshalFromSwift<ManagedFrozen>(new IntPtr(&result))", plan.PInvokeExpression);
    }

    [Fact]
    public void FrozenWithMemory_ReturnPlan_IndirectResult()
    {
        var proj = new FrozenWithMemoryProjection("ManagedFrozen");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.False(plan.RequiresUnsafe);
        Assert.Equal("SwiftMarshal.MarshalFromSwift<ManagedFrozen>(result)", plan.PInvokeExpression);
    }

    [Fact]
    public void FrozenWithMemory_ReturnPlan_OutBuffer()
    {
        var proj = new FrozenWithMemoryProjection("ManagedFrozen");
        var plan = proj.GetReturnPlan("_optRetPtr", ReturnStrategy.OutBuffer);

        Assert.Equal("SwiftMarshal.MarshalFromSwift<ManagedFrozen>(_optRetPtr)", plan.PInvokeExpression);
    }

    #endregion

    #region ClassProjection

    [Fact]
    public void Class_ParameterPlan_DangerousGetHandle()
    {
        var proj = new ClassProjection("ViewController");
        var plan = proj.GetParameterPlan("vc");

        Assert.Equal("vc.Payload.DangerousGetHandle()", plan.PInvokeExpression);
    }

    [Fact]
    public void Class_ReturnPlan_Direct_MarshalFromSwift()
    {
        var proj = new ClassProjection("ViewController");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        // ARC bridge: no buffer allocation, just MarshalFromSwift directly
        Assert.False(plan.RequiresUnsafe);
        Assert.Equal("(ViewController)SwiftMarshal.MarshalFromSwift<ViewController>(result)", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    #endregion

    #region Array (blittable element)

    [Fact]
    public void Array_Blittable_ParameterPlan_FromEnumerableDirect()
    {
        var proj = new ArrayProjection(new BlittableProjection("Int64"), isParameter: true);
        var plan = proj.GetParameterPlan("nums");

        Assert.Equal("numsBuffer", plan.PInvokeExpression);

        // Direct path: no Select, no try/finally
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("SwiftArray<Int64>.FromEnumerable(nums)", firstLine.Code);
        Assert.Contains("numsSwiftDirect", firstLine.Code);
    }

    [Fact]
    public void Array_Blittable_ReturnPlan_Direct_MarshalFromSwiftUnsafe()
    {
        var proj = new ArrayProjection(new BlittableProjection("Int64"), isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.True(plan.RequiresUnsafe);
        Assert.Contains("SwiftMarshal.MarshalFromSwift<SwiftArray<Int64>>(new IntPtr(&result))", plan.PInvokeExpression);
        Assert.Contains(".AsProjected(e => e)", plan.PInvokeExpression);
    }

    [Fact]
    public void Array_Blittable_ReturnPlan_IndirectResult()
    {
        var proj = new ArrayProjection(new BlittableProjection("Int64"), isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.False(plan.RequiresUnsafe);
        Assert.Contains("SwiftMarshal.MarshalFromSwift<SwiftArray<Int64>>(result)", plan.PInvokeExpression);
        Assert.Contains(".AsProjected(e => e)", plan.PInvokeExpression);
    }

    #endregion

    #region Array (string element)

    [Fact]
    public void Array_String_ParameterPlan_SelectConvertWithDisposal()
    {
        var proj = new ArrayProjection(new StringProjection(), isParameter: true);
        var plan = proj.GetParameterPlan("names");

        Assert.Equal("namesBuffer", plan.PInvokeExpression);

        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Select(e => new SwiftString(e)).ToList()", firstLine.Code);

        // Should have try/finally with disposal
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header == "finally");
        var finallyBlock = plan.SetupStatements.OfType<MarshalStatement.Block>().First(b => b.Header == "finally");
        var disposalLine = Assert.IsType<MarshalStatement.Line>(finallyBlock.Body[0]);
        Assert.Contains("Dispose()", disposalLine.Code);
    }

    [Fact]
    public void Array_String_ReturnPlan_AsProjectedWithToString()
    {
        var proj = new ArrayProjection(new StringProjection(), isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains("SwiftMarshal.MarshalFromSwift<SwiftArray<SwiftString>>(result)", plan.PInvokeExpression);
        Assert.Contains(".AsProjected(e => e.ToString())", plan.PInvokeExpression);
    }

    [Fact]
    public void Array_ReturnPlan_AsyncCallback_PassThrough()
    {
        var proj = new ArrayProjection(new BlittableProjection("Int64"), isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.AsyncCallback);

        Assert.Equal("result", plan.PInvokeExpression);
    }

    #endregion

    #region Dictionary

    [Fact]
    public void Dictionary_StringString_ParameterPlan_SelectKvpWithDisposal()
    {
        var proj = new DictionaryProjection(new StringProjection(), new StringProjection(), isParameter: true);
        var plan = proj.GetParameterPlan("dict");

        Assert.Equal("dictBuffer", plan.PInvokeExpression);

        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains(".Select(kvp => new KeyValuePair<SwiftString, SwiftString>", firstLine.Code);
        Assert.Contains("new SwiftString(kvp.Key)", firstLine.Code);
        Assert.Contains("new SwiftString(kvp.Value)", firstLine.Code);

        // Both key and value should be disposed
        var finallyBlocks = plan.SetupStatements.OfType<MarshalStatement.Block>().Where(b => b.Header == "finally").ToList();
        Assert.Single(finallyBlocks);
        Assert.Equal(2, finallyBlocks[0].Body.Count); // key + value disposal
    }

    [Fact]
    public void Dictionary_StringString_ReturnPlan_AsProjectedWithConversions()
    {
        var proj = new DictionaryProjection(new StringProjection(), new StringProjection(), isParameter: false);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains("SwiftMarshal.MarshalFromSwift<SwiftDictionary<SwiftString, SwiftString>>(result)", plan.PInvokeExpression);
        Assert.Contains(".AsProjected(k => k.ToString(), k => new SwiftString(k), v => v.ToString())", plan.PInvokeExpression);
    }

    #endregion

    #region Optional (blittable inner)

    [Fact]
    public void Optional_Blittable_ParameterPlan_InlineTernary()
    {
        var proj = new OptionalProjection(new BlittableProjection("Int64"));
        var plan = proj.GetParameterPlan("count");

        Assert.Equal("countBuffer", plan.PInvokeExpression);

        // Simple inner → inline ternary in Using
        var usingStmt = plan.SetupStatements.OfType<MarshalStatement.Using>()
            .First(u => u.Type.Contains("SwiftOptional"));
        Assert.Contains("NewSome(countValue)", usingStmt.InitExpression);
        Assert.Contains("NewNone()", usingStmt.InitExpression);
    }

    [Fact]
    public void Optional_Blittable_ReturnPlan_Direct_ToNullable()
    {
        var proj = new OptionalProjection(new BlittableProjection("Int64"));
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.True(plan.RequiresUnsafe);
        Assert.Contains("SwiftMarshal.MarshalFromSwift<SwiftOptional<Int64>>", plan.PInvokeExpression);
        Assert.Contains("ToNullable()", plan.PInvokeExpression);
    }

    [Fact]
    public void Optional_Blittable_ReturnPlan_IndirectResult_ToNullable()
    {
        var proj = new OptionalProjection(new BlittableProjection("Int64"));
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.False(plan.RequiresUnsafe);
        Assert.Contains("SwiftMarshal.MarshalFromSwift<SwiftOptional<Int64>>(result).ToNullable()", plan.PInvokeExpression);
    }

    #endregion

    #region Optional (string inner)

    [Fact]
    public void Optional_String_ParameterPlan_ComplexInner()
    {
        var proj = new OptionalProjection(new StringProjection());
        var plan = proj.GetParameterPlan("name");

        Assert.Equal("nameBuffer", plan.PInvokeExpression);

        // String inner needs complex path (element conversion)
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header.Contains("if ("));
        Assert.Contains(plan.SetupStatements, s => s is MarshalStatement.Block b && b.Header == "else");
    }

    [Fact]
    public void Optional_String_ReturnPlan_ConditionalConvert()
    {
        var proj = new OptionalProjection(new StringProjection());
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        // Should use rawOpt + conditional conversion
        var setupLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("MarshalFromSwift<SwiftOptional<SwiftString>>", setupLine.Code);
        Assert.Contains("ToNullable()", setupLine.Code);
        Assert.Contains("rawVal.ToString()", plan.PInvokeExpression);
    }

    #endregion

    #region Optional (existential inner)

    [Fact]
    public void Optional_Existential_ReturnPlan_DiscriminantCheck()
    {
        var inner = new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var proj = new OptionalProjection(inner, isExistentialInner: true);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains("SwiftOptionalCases.None", plan.PInvokeExpression);
        Assert.Contains("new DescribableProxy(swiftResult.Some)", plan.PInvokeExpression);
    }

    #endregion

    #region Optional (container inner)

    [Fact]
    public void Optional_Array_ReturnPlan_DiscriminantWithAsProjected()
    {
        var arrayProj = new ArrayProjection(new BlittableProjection("Int64"), isParameter: false);
        var proj = new OptionalProjection(arrayProj);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        Assert.Contains("SwiftOptionalCases.None", plan.PInvokeExpression);
        Assert.Contains("AsProjected", plan.PInvokeExpression);
    }

    #endregion

    #region Tuple (blittable)

    [Fact]
    public void Tuple_Blittable_ParameterPlan_PassThrough()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new BlittableProjection("Int64"),
            new BlittableProjection("Int32")
        });
        var plan = proj.GetParameterPlan("pair");

        Assert.Equal("pair", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    [Fact]
    public void Tuple_Blittable_ReturnPlan_PassThrough()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new BlittableProjection("Int64"),
            new BlittableProjection("Int32")
        });
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Equal("result", plan.PInvokeExpression);
    }

    #endregion

    #region Tuple (mixed - string + blittable)

    [Fact]
    public void Tuple_Mixed_ParameterPlan_PerElementConversion()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new StringProjection(),
            new BlittableProjection("Int64")
        });
        var plan = proj.GetParameterPlan("pair");

        // Non-passthrough: should have setup for string element conversion
        Assert.NotEmpty(plan.SetupStatements);
    }

    [Fact]
    public void Tuple_Mixed_ReturnPlan_PerElementConversion()
    {
        var proj = new TupleProjection(new ITypeProjection[]
        {
            new StringProjection(),
            new BlittableProjection("Int64")
        });
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        // Should have element conversion for the string element
        Assert.NotEmpty(plan.SetupStatements);
    }

    #endregion

    #region Existential

    [Fact]
    public void Existential_Proxy_ParameterPlan_GetExistentialContainer()
    {
        var proj = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var plan = proj.GetParameterPlan("item");

        Assert.Contains("ExistentialContainerFactory.GetOrCreate<IDescribable>(item)", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_Proxy_ReturnPlan_ConstructsProxy()
    {
        var proj = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IDescribable", "DescribableProxy");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Equal("new DescribableProxy(result)", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_WellKnown_ReturnPlan_ConstructsDirectly()
    {
        var proj = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "Swift.AnyError", null);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Equal("new Swift.AnyError(result)", plan.PInvokeExpression);
    }

    [Fact]
    public void Existential_Object_ReturnPlan_PassThrough()
    {
        var proj = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "object", null);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.Equal("result", plan.PInvokeExpression);
    }

    #endregion

    #region Closure

    [Fact]
    public void Closure_Escaping_ParameterPlan_GCHandleAndClosureData()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: new BoolProjection(),
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "myCallback");
        var plan = proj.GetParameterPlan("handler");

        Assert.Contains("GCHandle", plan.PInvokeExpression + string.Join(" ", plan.SetupStatements.OfType<MarshalStatement.Line>().Select(l => l.Code)));
    }

    [Fact]
    public void Closure_ReturnPlan_LambdaWithFuncPtrCheck()
    {
        var proj = new ClosureProjection(
            new ITypeProjection[] { new BlittableProjection("Int64") },
            returnProjection: new BoolProjection(),
            isEscaping: true,
            throws: false,
            isAsync: false,
            callbackName: "myCallback");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        Assert.True(plan.RequiresUnsafe);
        var firstLine = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("FunctionPointer == IntPtr.Zero", firstLine.Code);
    }

    #endregion

    #region Async

    [Fact]
    public void Async_ReturnPlan_AsyncCallback_SetupsTCS()
    {
        var proj = new AsyncProjection(new StringProjection(), throws: false, callbackPrefix: "test");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.AsyncCallback);

        var allCode = string.Join(" ", plan.SetupStatements.OfType<MarshalStatement.Line>().Select(l => l.Code));
        Assert.Contains("TaskCompletionSource", allCode);
        Assert.Contains("GCHandle", allCode);
    }

    #endregion

    #region Rendered Output Regression

    [Fact]
    public void Rendered_String_Direct_Return()
    {
        var proj = new StringProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        var output = Render(plan);

        Assert.Contains("SwiftMarshal.MarshalFromSwift<SwiftString>(new IntPtr(&result))", output);
        Assert.Contains("return swiftResult.ToString();", output);
    }

    [Fact]
    public void Rendered_String_IndirectResult_Return()
    {
        var proj = new StringProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);
        var output = Render(plan);

        Assert.Contains("return SwiftString.MarshalFromSwift(result);", output);
    }

    [Fact]
    public void Rendered_Class_Direct_Return()
    {
        var proj = new ClassProjection("ViewController");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        var output = Render(plan);

        // ARC bridge: direct MarshalFromSwift, no buffer allocation
        Assert.Contains("return (ViewController)SwiftMarshal.MarshalFromSwift<ViewController>(result);", output);
        Assert.DoesNotContain("NativeMemory", output);
        Assert.DoesNotContain("try", output);
        Assert.DoesNotContain("catch", output);
    }

    [Fact]
    public void Rendered_SimpleEnum_Param()
    {
        var proj = new SimpleEnumProjection("Status", "int");
        var plan = proj.GetParameterPlan("status");
        var output = RenderStatements(plan.SetupStatements);

        // SimpleEnum param has no setup — just a cast expression
        Assert.Equal("", output.Trim());
    }

    [Fact]
    public void Rendered_SimpleEnum_Return()
    {
        var proj = new SimpleEnumProjection("Status", "int");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        var output = Render(plan);

        Assert.Contains("return (Status)result;", output);
    }

    [Fact]
    public void Rendered_Array_String_Param()
    {
        var proj = new ArrayProjection(new StringProjection(), isParameter: true);
        var plan = proj.GetParameterPlan("names");
        var output = RenderStatements(plan.SetupStatements);

        Assert.Contains("names.Select(e => new SwiftString(e)).ToList()", output);
        Assert.Contains("SwiftArray<SwiftString>.FromEnumerable(namesConverted)", output);
        Assert.Contains("foreach (var _item in namesConverted) _item.Dispose()", output);
        Assert.Contains("using var namesSwift = namesSwiftInner;", output);
        Assert.Contains("using var namesDisposable = namesSwift.PayloadBuffer;", output);
        Assert.Contains("IntPtr namesBuffer = namesDisposable.Buffer;", output);
    }

    [Fact]
    public void Rendered_Optional_Blittable_Param()
    {
        var proj = new OptionalProjection(new BlittableProjection("Int64"));
        var plan = proj.GetParameterPlan("count");
        var output = RenderStatements(plan.SetupStatements);

        Assert.Contains("SwiftOptional<Int64>", output);
        Assert.Contains("NewSome(countValue)", output);
        Assert.Contains("NewNone()", output);
        Assert.Contains("using var countDisposable = countSwift.PayloadBuffer;", output);
        Assert.Contains("IntPtr countBuffer = countDisposable.Buffer;", output);
    }

    #endregion

    #region Helpers

    private static string Render(MarshalPlan plan)
    {
        using var sw = new StringWriter();
        var writer = new CSharpWriter(sw);
        MarshalPlanRenderer.RenderReturnPlan(writer, plan);
        writer.Flush();
        return sw.ToString();
    }

    private static string RenderStatements(IReadOnlyList<MarshalStatement> statements)
    {
        using var sw = new StringWriter();
        var writer = new CSharpWriter(sw);
        MarshalPlanRenderer.RenderStatements(writer, statements);
        writer.Flush();
        return sw.ToString();
    }

    #endregion
}
