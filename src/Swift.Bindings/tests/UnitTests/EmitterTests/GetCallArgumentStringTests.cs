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
        var param = new Parameter("SafeHandle", "loader");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("loader.Payload", result);
    }

    [Fact]
    public void GetCallArgumentString_EnumSafeHandle_ReturnsDangerousGetHandle()
    {
        var param = new Parameter("EnumSafeHandle", "status");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("status.Payload.DangerousGetHandle()", result);
    }

    [Fact]
    public void GetCallArgumentString_SimpleEnumInt64_ReturnsCast()
    {
        var param = new Parameter("SimpleEnum:Int64:Direction", "direction");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("(Int64)direction", result);
    }

    [Fact]
    public void GetCallArgumentString_ExistentialContainer_ReturnsConversion()
    {
        var param = new Parameter("Existential:ExistentialContainer1:IMyProtocol", "handler");
        var result = Signature.GetCallArgumentString(param);
        Assert.Contains("ISwiftExistentialConvertible<ExistentialContainer1>", result);
        Assert.Contains("GetExistentialContainer()", result);
    }

    [Fact]
    public void GetCallArgumentString_IntPtrFromNonFrozen_ReturnsHandle()
    {
        var param = new Parameter("IntPtrFromNonFrozen", "response");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("responseHandle", result);
    }

    [Fact]
    public void GetCallArgumentString_BufferRef_ReturnsBufferRefDisposable()
    {
        var param = new Parameter("Point.Buffer", "point", "ref");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("ref pointDisposable.BufferRef", result);
    }

    [Fact]
    public void GetCallArgumentString_BufferNonRef_ReturnsBuffer()
    {
        var param = new Parameter("Point.Buffer", "point");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("pointDisposable.Buffer", result);
    }

    [Fact]
    public void GetCallArgumentString_OutModifier_ReturnsOutVar()
    {
        var param = new Parameter("Int64", "result", "out");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("out var result", result);
    }

    [Fact]
    public void GetCallArgumentString_RefModifier_ReturnsRef()
    {
        var param = new Parameter("Int64", "value", "ref");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("ref value", result);
    }

    [Fact]
    public void GetCallArgumentString_SwiftClosureData_ReturnsClosure()
    {
        var param = new Parameter("SwiftClosureData", "callback");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("callbackClosure", result);
    }

    [Fact]
    public void GetCallArgumentString_DelegateUnmanaged_ReturnsFuncPtr()
    {
        var param = new Parameter("delegate* unmanaged[Cdecl]<long, void>", "callback");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("callbackFuncPtr", result);
    }

    [Fact]
    public void GetCallArgumentString_SelfClass_ReturnsPayloadDeref()
    {
        var param = new Parameter("IntPtr", "_selfClass");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("*(IntPtr*)_payload.DangerousGetHandle()", result);
    }

    [Fact]
    public void GetCallArgumentString_SelfFixed_ReturnsCastSelf()
    {
        var param = new Parameter("IntPtr", "_selfFixed");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("(IntPtr)__self", result);
    }

    [Fact]
    public void GetCallArgumentString_SelfIntPtr_ReturnsPayloadHandle()
    {
        var param = new Parameter("IntPtr", "_self");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("_payload.DangerousGetHandle()", result);
    }

    [Fact]
    public void GetCallArgumentString_PlainType_ReturnsName()
    {
        var param = new Parameter("Int64", "count");
        var result = Signature.GetCallArgumentString(param);
        Assert.Equal("count", result);
    }
}
