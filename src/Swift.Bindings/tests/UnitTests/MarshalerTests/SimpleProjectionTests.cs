// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for simple type projections — verifies each projection produces
/// correct parameter plans, return plans, and type metadata.
/// </summary>
public class SimpleProjectionTests
{
    #region BlittableProjection

    [Fact]
    public void Blittable_PublicAndPInvokeTypesMatch()
    {
        var proj = new BlittableProjection("Int64");
        Assert.Equal("Int64", proj.PublicType);
        Assert.Equal("Int64", proj.PInvokeType);
    }

    [Fact]
    public void Blittable_PInvokeAttribute_IsNull()
    {
        var proj = new BlittableProjection("Int64");
        Assert.Null(proj.PInvokeAttribute);
    }

    [Fact]
    public void Blittable_ParameterPlan_IsPassThrough()
    {
        var proj = new BlittableProjection("Int64");
        var plan = proj.GetParameterPlan("x");
        Assert.Equal("x", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
        Assert.Empty(plan.CleanupStatements);
        Assert.Empty(plan.UsingDeclarations);
    }

    [Fact]
    public void Blittable_ReturnPlan_IsPassThrough()
    {
        var proj = new BlittableProjection("Int64");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("result", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    [Fact]
    public void Blittable_DoesNotRequireSwiftWrapper()
    {
        var proj = new BlittableProjection("Int64");
        Assert.False(proj.RequiresSwiftWrapper);
        Assert.Null(proj.GetSwiftWrapperCode(new SwiftWrapperContext()));
    }

    #endregion

    #region BoolProjection

    [Fact]
    public void Bool_Types()
    {
        var proj = new BoolProjection();
        Assert.Equal("bool", proj.PublicType);
        Assert.Equal("bool", proj.PInvokeType);
    }

    [Fact]
    public void Bool_PInvokeAttribute_IsMarshalAs()
    {
        var proj = new BoolProjection();
        // Qualified: the attribute is emitted into `namespace <SwiftModule>`, where a bound
        // library's own `MarshalAs`/`UnmanagedType` would otherwise capture it.
        Assert.Equal(
            "[global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.U1)]",
            proj.PInvokeAttribute);
    }

    [Fact]
    public void Bool_ParameterPlan_IsPassThrough()
    {
        var proj = new BoolProjection();
        var plan = proj.GetParameterPlan("flag");
        Assert.Equal("flag", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    [Fact]
    public void Bool_ReturnPlan_IsPassThrough()
    {
        var proj = new BoolProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("result", plan.PInvokeExpression);
    }

    [Fact]
    public void Bool_DoesNotRequireSwiftWrapper()
    {
        var proj = new BoolProjection();
        Assert.False(proj.RequiresSwiftWrapper);
    }

    #endregion

    #region StringProjection

    [Fact]
    public void String_Types()
    {
        var proj = new StringProjection();
        Assert.Equal("string", proj.PublicType);
        Assert.Equal("SwiftString", proj.PInvokeType);
    }

    [Fact]
    public void String_PInvokeAttribute_IsNull()
    {
        var proj = new StringProjection();
        Assert.Null(proj.PInvokeAttribute);
    }

    [Fact]
    public void String_ParameterPlan_HasUsingStatement()
    {
        var proj = new StringProjection();
        var plan = proj.GetParameterPlan("name");

        Assert.Equal("nameDisposable.Buffer", plan.PInvokeExpression);
        Assert.Equal(2, plan.SetupStatements.Count);

        var setup = Assert.IsType<MarshalStatement.Using>(plan.SetupStatements[0]);
        Assert.Equal("SwiftString", setup.Type);
        Assert.Equal("nameSwift", setup.Name);
        Assert.Equal("new SwiftString(name)", setup.InitExpression);

        var payloadUsing = Assert.IsType<MarshalStatement.Using>(plan.SetupStatements[1]);
        Assert.Equal("PayloadBuffer<SwiftString.Buffer>", payloadUsing.Type);
        Assert.Equal("nameDisposable", payloadUsing.Name);
        Assert.Equal("nameSwift.PayloadBuffer", payloadUsing.InitExpression);
    }

    [Fact]
    public void String_ParameterPlan_HasUsingDeclaration()
    {
        var proj = new StringProjection();
        var plan = proj.GetParameterPlan("name");

        // PayloadBuffer extraction is now in SetupStatements, not UsingDeclarations
        Assert.Empty(plan.UsingDeclarations);
    }

    [Fact]
    public void String_ReturnPlan_Direct_UsesToString()
    {
        var proj = new StringProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("swiftResult.ToString()", plan.PInvokeExpression);
        Assert.True(plan.RequiresUnsafe);
        Assert.Single(plan.SetupStatements);
        Assert.Contains("MarshalFromSwiftObject<SwiftString>", ((MarshalStatement.Line)plan.SetupStatements[0]).Code);
    }

    [Fact]
    public void String_ReturnPlan_IndirectResult_UsesMarshalFromSwift()
    {
        var proj = new StringProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);
        Assert.Equal("SwiftString.MarshalFromSwift(result)", plan.PInvokeExpression);
    }

    [Fact]
    public void String_ReturnPlan_OutBuffer_FallsThrough()
    {
        var proj = new StringProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.OutBuffer);
        Assert.Equal("result", plan.PInvokeExpression);
    }

    [Fact]
    public void String_DoesNotRequireSwiftWrapper()
    {
        var proj = new StringProjection();
        Assert.False(proj.RequiresSwiftWrapper);
    }

    #endregion

    #region SimpleEnumProjection

    [Fact]
    public void SimpleEnum_Types()
    {
        var proj = new SimpleEnumProjection("Direction", "int");
        Assert.Equal("Direction", proj.PublicType);
        Assert.Equal("int", proj.PInvokeType);
    }

    [Fact]
    public void SimpleEnum_PInvokeAttribute_IsNull()
    {
        var proj = new SimpleEnumProjection("Direction", "int");
        Assert.Null(proj.PInvokeAttribute);
    }

    [Fact]
    public void SimpleEnum_ParameterPlan_CastsToUnderlying()
    {
        var proj = new SimpleEnumProjection("Direction", "int");
        var plan = proj.GetParameterPlan("dir");
        Assert.Equal("(int)dir", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    [Fact]
    public void SimpleEnum_ReturnPlan_CastsToEnum()
    {
        var proj = new SimpleEnumProjection("Direction", "int");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("(Direction)result", plan.PInvokeExpression);
    }

    [Fact]
    public void SimpleEnum_DoesNotRequireSwiftWrapper()
    {
        var proj = new SimpleEnumProjection("Direction", "int");
        Assert.False(proj.RequiresSwiftWrapper);
    }

    #endregion

    #region ObjCBridgedProjection

    [Fact]
    public void ObjCBridged_Types()
    {
        var proj = new ObjCBridgedProjection("UIImage");
        Assert.Equal("UIImage", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void ObjCBridged_PInvokeAttribute_IsNull()
    {
        var proj = new ObjCBridgedProjection("UIImage");
        Assert.Null(proj.PInvokeAttribute);
    }

    [Fact]
    public void ObjCBridged_ParameterPlan_ExtractsHandle()
    {
        var proj = new ObjCBridgedProjection("UIImage");
        var plan = proj.GetParameterPlan("image");

        Assert.Equal("imageHandle", plan.PInvokeExpression);
        Assert.Single(plan.SetupStatements);

        var setup = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Equal("var imageHandle = image.Handle;", setup.Code);
    }

    [Fact]
    public void ObjCBridged_ReturnPlan_WrapsWithGetNSObject()
    {
        var proj = new ObjCBridgedProjection("UIImage");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("ObjCRuntime.Runtime.GetNSObject<UIImage>(result)!", plan.PInvokeExpression);
    }

    [Fact]
    public void ObjCBridged_DoesNotRequireSwiftWrapper()
    {
        var proj = new ObjCBridgedProjection("UIImage");
        Assert.False(proj.RequiresSwiftWrapper);
    }

    #endregion

    #region DataProjection

    [Fact]
    public void DataProjection_PublicType_IsByteArray()
    {
        var proj = new DataProjection();
        Assert.Equal("byte[]", proj.PublicType);
        Assert.Equal("Swift.Foundation.Data", proj.PInvokeType);
    }

    [Fact]
    public void DataProjection_ParameterPlan_CreatesFromByteArray()
    {
        var proj = new DataProjection();
        var plan = proj.GetParameterPlan("data");

        Assert.Equal("dataSwift", plan.PInvokeExpression);
        Assert.Single(plan.SetupStatements);

        var setup = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("Swift.Foundation.Data.FromByteArray(data)", setup.Code);
    }

    [Fact]
    public void DataProjection_ReturnPlan_Direct_ToByteArray()
    {
        var proj = new DataProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("result.ToByteArray()", plan.PInvokeExpression);
    }

    [Fact]
    public void DataProjection_ReturnPlan_IndirectResult_MarshalFromSwift()
    {
        var proj = new DataProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);
        Assert.Equal("SwiftMarshal.MarshalFromSwiftObject<Swift.Foundation.Data>(result).ToByteArray()", plan.PInvokeExpression);
    }

    [Fact]
    public void DataProjection_ElementConversions()
    {
        var proj = new DataProjection();
        Assert.Equal("Swift.Foundation.Data.FromByteArray(e)", proj.GetParameterElementConversion("e"));
        Assert.Equal("e.ToByteArray()", proj.GetReturnElementConversion("e"));
        Assert.False(proj.ElementRequiresDisposal);
    }

    [Fact]
    public void DataProjection_DoesNotRequireSwiftWrapper()
    {
        var proj = new DataProjection();
        Assert.False(proj.RequiresSwiftWrapper);
    }

    #endregion

    #region DateProjection

    [Fact]
    public void DateProjection_Types()
    {
        var proj = new DateProjection();
        Assert.Equal("System.DateTimeOffset", proj.PublicType);
        Assert.Equal("double", proj.PInvokeType);
    }

    [Fact]
    public void DateProjection_PInvokeAttribute_IsNull()
    {
        var proj = new DateProjection();
        Assert.Null(proj.PInvokeAttribute);
    }

    [Fact]
    public void DateProjection_ParameterPlan_ConvertsTotalSeconds()
    {
        var proj = new DateProjection();
        var plan = proj.GetParameterPlan("date");

        Assert.Equal("dateSwift", plan.PInvokeExpression);
        Assert.Single(plan.SetupStatements);

        var setup = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("TotalSeconds", setup.Code);
        Assert.Contains("2001, 1, 1", setup.Code);
    }

    [Fact]
    public void DateProjection_ReturnPlan_Direct_AddsSeconds()
    {
        var proj = new DateProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Contains("AddSeconds(result)", plan.PInvokeExpression);
        Assert.Contains("2001, 1, 1", plan.PInvokeExpression);
    }

    [Fact]
    public void DateProjection_ReturnPlan_IndirectResult_MarshalFromPointer()
    {
        var proj = new DateProjection();
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);
        Assert.Contains("AddSeconds", plan.PInvokeExpression);
        Assert.Contains("PtrToStructure<double>", plan.PInvokeExpression);
    }

    [Fact]
    public void DateProjection_ElementConversions()
    {
        var proj = new DateProjection();

        var paramConv = proj.GetParameterElementConversion("e");
        Assert.NotNull(paramConv);
        Assert.Contains("TotalSeconds", paramConv);

        var returnConv = proj.GetReturnElementConversion("e");
        Assert.NotNull(returnConv);
        Assert.Contains("AddSeconds", returnConv);

        Assert.False(proj.ElementRequiresDisposal);
    }

    [Fact]
    public void DateProjection_DoesNotRequireSwiftWrapper()
    {
        var proj = new DateProjection();
        Assert.False(proj.RequiresSwiftWrapper);
        Assert.Null(proj.GetSwiftWrapperCode(new SwiftWrapperContext()));
    }

    #endregion

    #region NativeRemappedProjection

    [Fact]
    public void NativeRemapped_Frozen_Types()
    {
        var proj = new NativeRemappedProjection("NSUrl", "SwiftURL", isFrozen: true, toConversionMethod: "ToNSUrl");
        Assert.Equal("NSUrl", proj.PublicType);
        Assert.Equal("SwiftURL", proj.PInvokeType);
    }

    [Fact]
    public void NativeRemapped_NonFrozen_Types()
    {
        var proj = new NativeRemappedProjection("NSUrl", "SwiftURL", isFrozen: false, toConversionMethod: "ToNSUrl");
        Assert.Equal("NSUrl", proj.PublicType);
        Assert.Equal("SafeHandle", proj.PInvokeType);
    }

    [Fact]
    public void NativeRemapped_Frozen_ParameterPlan_UsesVarNotUsing()
    {
        var proj = new NativeRemappedProjection("NSUrl", "SwiftURL", isFrozen: true, toConversionMethod: "ToNSUrl");
        var plan = proj.GetParameterPlan("url");

        Assert.Equal("urlSwift", plan.PInvokeExpression);
        Assert.Single(plan.SetupStatements);

        // Frozen types use var (no using) — no disposal needed for value types
        var setup = Assert.IsType<MarshalStatement.Line>(plan.SetupStatements[0]);
        Assert.Contains("urlSwift", setup.Code);
        Assert.Contains("new SwiftURL(url)", setup.Code);
    }

    [Fact]
    public void NativeRemapped_NonFrozen_ParameterPlan_UsesPayload()
    {
        var proj = new NativeRemappedProjection("NSUrl", "SwiftURL", isFrozen: false, toConversionMethod: "ToNSUrl");
        var plan = proj.GetParameterPlan("url");

        Assert.Equal("urlSwift.Payload", plan.PInvokeExpression);
        Assert.Single(plan.SetupStatements);

        var setup = Assert.IsType<MarshalStatement.Using>(plan.SetupStatements[0]);
        Assert.Equal("SwiftURL", setup.Type);
    }

    [Fact]
    public void NativeRemapped_ReturnPlan_ConvertsBack()
    {
        var proj = new NativeRemappedProjection("NSUrl", "SwiftURL", isFrozen: true, toConversionMethod: "ToNSUrl");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("result.ToNSUrl()", plan.PInvokeExpression);
    }

    [Fact]
    public void NativeRemapped_DoesNotRequireSwiftWrapper()
    {
        var proj = new NativeRemappedProjection("NSUrl", "SwiftURL", isFrozen: true, toConversionMethod: "ToNSUrl");
        Assert.False(proj.RequiresSwiftWrapper);
    }

    #endregion

    #region NonFrozenStructProjection

    [Fact]
    public void NonFrozenStruct_Types()
    {
        var proj = new NonFrozenStructProjection("MyClass");
        Assert.Equal("MyClass", proj.PublicType);
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void NonFrozenStruct_PInvokeAttribute_IsNull()
    {
        var proj = new NonFrozenStructProjection("MyClass");
        Assert.Null(proj.PInvokeAttribute);
    }

    [Fact]
    public void NonFrozenStruct_ParameterPlan_ExtractsPayloadHandle()
    {
        var proj = new NonFrozenStructProjection("MyClass");
        var plan = proj.GetParameterPlan("obj");
        Assert.Equal("obj.Payload.DangerousGetHandle()", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
    }

    [Fact]
    public void NonFrozenStruct_ReturnPlan_UsesMarshalFromSwift()
    {
        var proj = new NonFrozenStructProjection("MyClass");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);
        Assert.Equal("SwiftMarshal.MarshalFromSwiftObject<MyClass>(result)", plan.PInvokeExpression);
    }

    [Fact]
    public void NonFrozenStruct_DoesNotRequireSwiftWrapper()
    {
        var proj = new NonFrozenStructProjection("MyClass");
        Assert.False(proj.RequiresSwiftWrapper);
    }

    #endregion

    #region MarshalPlan Static Factory

    [Fact]
    public void PassThrough_CreatesSimplePlan()
    {
        var plan = MarshalPlan.PassThrough("x");
        Assert.Equal("x", plan.PInvokeExpression);
        Assert.Empty(plan.SetupStatements);
        Assert.Empty(plan.CleanupStatements);
        Assert.Empty(plan.UsingDeclarations);
        Assert.False(plan.RequiresUnsafe);
        Assert.False(plan.RequiresFixed);
    }

    #endregion
}
