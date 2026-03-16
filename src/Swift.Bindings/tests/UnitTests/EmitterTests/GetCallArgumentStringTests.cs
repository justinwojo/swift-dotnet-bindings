// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for Signature.GetCallArgumentString — all 25+ pattern branches.
/// </summary>
public class GetCallArgumentStringTests
{
    [Fact]
    public void GetCallArgumentString_SafeHandle_ReturnsPayload()
    {
        var param = new Parameter(MarshalledType.NonFrozenSafeHandle, "loader");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("loader.Payload", result);
    }

    [Fact]
    public void GetCallArgumentString_EnumSafeHandle_ReturnsDangerousGetHandle()
    {
        var param = new Parameter(MarshalledType.EnumSafeHandle, "status");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("status.Payload.DangerousGetHandle()", result);
    }

    [Fact]
    public void GetCallArgumentString_SimpleEnumInt64_ReturnsCast()
    {
        var param = new Parameter(new MarshalledType.SimpleEnum("Int64", "Direction"), "direction");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("(Int64)direction", result);
    }

    [Fact]
    public void GetCallArgumentString_ExistentialContainer_ReturnsConversion()
    {
        var param = new Parameter(new MarshalledType.Existential("Swift.Runtime.ExistentialContainer1", "IMyProtocol"), "handler");
        var result = Signature.GetCallArgumentString(param);
        Assert.Contains("ExistentialContainerFactory.GetOrCreate<IMyProtocol>", result);
    }

    [Fact]
    public void GetCallArgumentString_IntPtrFromNonFrozen_ReturnsHandle()
    {
        var param = new Parameter(MarshalledType.NonFrozenIntPtr, "response");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("responseHandle", result);
    }

    [Fact]
    public void GetCallArgumentString_BufferRef_ReturnsBufferRefDisposable()
    {
        var param = new Parameter(new MarshalledType.FrozenBuffer("Point"), "point", "ref");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("ref pointDisposable.BufferRef", result);
    }

    [Fact]
    public void GetCallArgumentString_BufferNonRef_ReturnsBuffer()
    {
        var param = new Parameter(new MarshalledType.FrozenBuffer("Point"), "point");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("pointDisposable.Buffer", result);
    }

    [Fact]
    public void GetCallArgumentString_OutModifier_ReturnsOutVar()
    {
        var param = new Parameter(new MarshalledType.Simple("Int64"), "result", "out");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("out var result", result);
    }

    [Fact]
    public void GetCallArgumentString_RefModifier_ReturnsRef()
    {
        var param = new Parameter(new MarshalledType.Simple("Int64"), "value", "ref");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("ref value", result);
    }

    [Fact]
    public void GetCallArgumentString_SwiftClosureData_ReturnsClosure()
    {
        var param = new Parameter(MarshalledType.SwiftClosureLegacy, "callback");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("callbackClosure", result);
    }

    [Fact]
    public void GetCallArgumentString_DelegateUnmanaged_ReturnsFuncPtr()
    {
        var param = new Parameter(new MarshalledType.ConventionCFuncPtr("delegate* unmanaged[Cdecl]<long, void>"), "callback");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("callbackFuncPtr", result);
    }

    [Fact]
    public void GetCallArgumentString_SelfClass_ReturnsHandleDeref()
    {
        var param = new Parameter(new MarshalledType.Simple("IntPtr"), "_selfClass");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("_handle.DangerousGetHandle()", result);
    }

    [Fact]
    public void GetCallArgumentString_SelfFixed_ReturnsCastSelf()
    {
        var param = new Parameter(new MarshalledType.Simple("IntPtr"), "_selfFixed");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("(IntPtr)__self", result);
    }

    [Fact]
    public void GetCallArgumentString_SelfIntPtr_ReturnsPayloadHandle()
    {
        var param = new Parameter(new MarshalledType.Simple("IntPtr"), "_self");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("_payload.DangerousGetHandle()", result);
    }

    [Fact]
    public void GetCallArgumentString_PlainType_ReturnsName()
    {
        var param = new Parameter(new MarshalledType.Simple("Int64"), "count");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("count", result);
    }

    [Fact]
    public void GetCallArgumentString_AsyncCallback_ReturnsName()
    {
        var param = new Parameter(MarshalledType.AsyncCallback, "onComplete");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("onComplete", result);
    }

    [Fact]
    public void GetCallArgumentString_AsyncErrorCallback_ReturnsName()
    {
        var param = new Parameter(MarshalledType.AsyncErrorCallback, "onError");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("onError", result);
    }

    [Fact]
    public void GetCallArgumentString_AsyncContext_ReturnsNull()
    {
        var param = new Parameter(MarshalledType.AsyncContext, "context");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("null", result);
    }

    [Fact]
    public void GetCallArgumentString_AsyncTask_ReturnsGCHandleConversion()
    {
        var param = new Parameter(MarshalledType.AsyncTask, "task");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("GCHandle.ToIntPtr(task)", result);
    }

    [Fact]
    public void GetCallArgumentString_CdeclClosureFuncPtr_ReturnsHandleGuard()
    {
        var param = new Parameter(new MarshalledType.CdeclClosureFuncPtr("onComplete", "handler"), "funcPtr");
        var result = Signature.GetCallArgumentString(param);
        Assert.Contains("handlerHandle.IsAllocated", result);
        Assert.Contains("s_onComplete", result);
        Assert.Contains("IntPtr.Zero", result);
    }

    [Fact]
    public void GetCallArgumentString_CdeclClosureContext_ReturnsHandleGuard()
    {
        var param = new Parameter(new MarshalledType.CdeclClosureContext("handler"), "context");
        var result = Signature.GetCallArgumentString(param);
        Assert.Contains("handlerHandle.IsAllocated", result);
        Assert.Contains("GCHandle.ToIntPtr(handlerHandle)", result);
        Assert.Contains("IntPtr.Zero", result);
    }

    [Fact]
    public void GetCallArgumentString_AsyncThrowingContext_ReturnsContextPtr()
    {
        var param = new Parameter(new MarshalledType.AsyncThrowingContext("callback"), "ctx");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("callbackContextPtr", result);
    }

    [Fact]
    public void GetCallArgumentString_AsyncThrowingStartFunc_ReturnsStartFunc()
    {
        var param = new Parameter(new MarshalledType.AsyncThrowingStartFunc("onStart"), "startFunc");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("s_onStart_Start", result);
    }

    [Fact]
    public void GetCallArgumentString_ObjCBridged_ReturnsHandle()
    {
        var param = new Parameter(new MarshalledType.ObjCBridged("UIImage"), "image");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("imageHandle", result);
    }

    [Fact]
    public void GetCallArgumentString_NativeRemappedSafeHandle_ReturnsSwiftPayload()
    {
        var param = new Parameter(MarshalledType.NativeRemappedNonFrozen, "url");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("urlSwift.Payload", result);
    }

    [Fact]
    public void GetCallArgumentString_NativeRemapped_ReturnsSwiftSuffix()
    {
        var param = new Parameter(new MarshalledType.NativeRemappedFrozen("URL"), "url");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("urlSwift", result);
    }
}
