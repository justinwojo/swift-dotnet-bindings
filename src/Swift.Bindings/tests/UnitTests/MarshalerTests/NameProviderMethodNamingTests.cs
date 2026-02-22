// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for NameProvider.GetPublicMethodName() — verb prefix, async prefix stripping,
/// and double-async prevention.
/// </summary>
public class NameProviderMethodNamingTests
{
    #region Noun-only + sync → Get prefix (async skips Get prefix)

    [Fact]
    public void NounOnly_Async_WithReturn_SkipsGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("data", isAsync: true, hasReturnValue: true);
        Assert.Equal("DataAsync", result);
    }

    [Fact]
    public void NounOnly_Image_Async_WithReturn_SkipsGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("image", isAsync: true, hasReturnValue: true);
        Assert.Equal("ImageAsync", result);
    }

    [Fact]
    public void NounOnly_Response_Async_WithReturn_SkipsGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("response", isAsync: true, hasReturnValue: true);
        Assert.Equal("ResponseAsync", result);
    }

    [Fact]
    public void NounOnly_Sync_WithReturn_GetsGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("count", isAsync: false, hasReturnValue: true);
        Assert.Equal("GetCount", result);
    }

    #endregion

    #region Double async stripping

    [Fact]
    public void AsyncPrefix_CamelCase_Stripped()
    {
        var result = NameProvider.GetPublicMethodName("asyncGetString", isAsync: true, hasReturnValue: true);
        Assert.Equal("GetStringAsync", result);
    }

    [Fact]
    public void AsyncPrefix_PascalCase_Stripped_NoGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("AsyncStaticString", isAsync: true, hasReturnValue: true);
        Assert.Equal("StaticStringAsync", result);
    }

    [Fact]
    public void AsyncPrefix_WithReturnValue_NounSkipsGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("asyncData", isAsync: true, hasReturnValue: true);
        Assert.Equal("DataAsync", result);
    }

    [Fact]
    public void AsyncPrefix_NotStripped_WhenNotAsync()
    {
        // A sync property/method named "asyncInstance" should keep the prefix.
        // Without this gate, property getter naming breaks: asyncInstance_Get → Instance_Get (collision).
        var result = NameProvider.GetPublicMethodName("asyncInstance", isAsync: false, hasReturnValue: true);
        Assert.Equal("GetAsyncInstance", result);
    }

    [Fact]
    public void AsyncPrefix_StillStripped_WhenAsync_NoGetPrefix()
    {
        // Async methods should still have the prefix stripped per .NET convention.
        // Async methods skip the "Get" prefix — noun-only names are fine with Async suffix.
        var result = NameProvider.GetPublicMethodName("asyncInstance", isAsync: true, hasReturnValue: true);
        Assert.Equal("InstanceAsync", result);
    }

    #endregion

    #region Verb already present (no change)

    [Fact]
    public void VerbPrefix_LoadImage_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("loadImage", isAsync: true, hasReturnValue: true);
        Assert.Equal("LoadImageAsync", result);
    }

    [Fact]
    public void VerbPrefix_RemoveAll_Sync_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("removeAll", isAsync: false, hasReturnValue: false);
        Assert.Equal("RemoveAll", result);
    }

    [Fact]
    public void VerbPrefix_CreateImage_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("createImage", isAsync: false, hasReturnValue: true);
        Assert.Equal("CreateImage", result);
    }

    [Fact]
    public void VerbPrefix_IsValid_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("isValid", isAsync: false, hasReturnValue: true);
        Assert.Equal("IsValid", result);
    }

    [Fact]
    public void VerbPrefix_HasData_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("hasData", isAsync: false, hasReturnValue: true);
        Assert.Equal("HasData", result);
    }

    [Fact]
    public void VerbPrefix_RefreshTitle_Async_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("refreshTitle", isAsync: true, hasReturnValue: true);
        Assert.Equal("RefreshTitleAsync", result);
    }

    [Fact]
    public void AcceptsPrefix_RecognizedAsVerb()
    {
        var result = NameProvider.GetPublicMethodName("acceptsParameters", isAsync: false, hasReturnValue: true);
        Assert.Equal("AcceptsParameters", result);
    }

    [Fact]
    public void SumPrefix_RecognizedAsVerb()
    {
        var result = NameProvider.GetPublicMethodName("sum", isAsync: false, hasReturnValue: true);
        Assert.Equal("Sum", result);
    }

    [Fact]
    public void PassPrefix_RecognizedAsVerb()
    {
        var result = NameProvider.GetPublicMethodName("passThrough", isAsync: false, hasReturnValue: true);
        Assert.Equal("PassThrough", result);
    }

    #endregion

    #region Void return (no Get)

    [Fact]
    public void VoidReturn_NounOnly_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("flush", isAsync: false, hasReturnValue: false);
        Assert.Equal("Flush", result);
    }

    [Fact]
    public void VoidReturn_Count_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("count", isAsync: false, hasReturnValue: false);
        Assert.Equal("Count", result);
    }

    #endregion

    #region Property collision + verb prefix

    [Fact]
    public void PropertyCollision_WithReturn_GetsGetPrefixAndMethodSuffix()
    {
        var props = new HashSet<string> { "Data" };
        // "data" with return → "GetData" which doesn't collide with "Data" property
        var result = NameProvider.GetPublicMethodName("data", isAsync: false, hasReturnValue: true, props);
        Assert.Equal("GetData", result);
    }

    [Fact]
    public void PropertyCollision_WithVerb_MethodSuffix()
    {
        var props = new HashSet<string> { "GetData" };
        // "getData" → "GetData" → collides with property → "GetDataMethod"
        var result = NameProvider.GetPublicMethodName("getData", isAsync: false, hasReturnValue: true, props);
        Assert.Equal("GetDataMethod", result);
    }

    #endregion

    #region hasReturnValue = false by default (backward compatibility)

    [Fact]
    public void DefaultHasReturnValue_IsFalse_NoGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("data", isAsync: false);
        Assert.Equal("Data", result);
    }

    #endregion
}
