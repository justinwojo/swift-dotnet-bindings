// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Characterization tests for Parameter.SignatureString(), PInvokeSignatureString(),
/// ContainsPlaceholder, and CallString(). These lock down current behavior for
/// the MarshalledType-based Parameter type.
/// </summary>
public class ParameterSignatureTests
{
    #region SignatureString() Tests

    [Fact]
    public void SignatureString_AsyncCallback_ReturnsVoidPointer()
    {
        var param = new Parameter(MarshalledType.AsyncCallback, "onComplete");
        Assert.Equal(" void* onComplete", param.SignatureString());
    }

    [Fact]
    public void SignatureString_AsyncErrorCallback_ReturnsVoidPointer()
    {
        var param = new Parameter(MarshalledType.AsyncErrorCallback, "onError");
        Assert.Equal(" void* onError", param.SignatureString());
    }

    [Fact]
    public void SignatureString_AsyncContext_ReturnsVoidPointer()
    {
        var param = new Parameter(MarshalledType.AsyncContext, "ctx");
        Assert.Equal(" void* ctx", param.SignatureString());
    }

    [Fact]
    public void SignatureString_AsyncTask_ReturnsIntPtr()
    {
        var param = new Parameter(MarshalledType.AsyncTask, "handle");
        Assert.Equal(" IntPtr handle", param.SignatureString());
    }

    [Fact]
    public void SignatureString_IntPtrFromNonFrozen_ReturnsIntPtr()
    {
        var param = new Parameter(MarshalledType.NonFrozenIntPtr, "response");
        Assert.Equal(" IntPtr response", param.SignatureString());
    }

    [Fact]
    public void SignatureString_ObjCBridged_ReturnsIntPtr()
    {
        var param = new Parameter(new MarshalledType.ObjCBridged("UIImage"), "image");
        Assert.Equal(" IntPtr image", param.SignatureString());
    }

    [Fact]
    public void SignatureString_EnumSafeHandle_ReturnsIntPtr()
    {
        var param = new Parameter(MarshalledType.EnumSafeHandle, "status");
        Assert.Equal(" IntPtr status", param.SignatureString());
    }

    [Fact]
    public void SignatureString_SimpleEnum_ReturnsUnderlyingType()
    {
        var param = new Parameter(new MarshalledType.SimpleEnum("Int64", "Direction"), "direction");
        Assert.Equal(" Int64 direction", param.SignatureString());
    }

    [Fact]
    public void SignatureString_SimpleEnum_IntUnderlyingType()
    {
        var param = new Parameter(new MarshalledType.SimpleEnum("int", "Status"), "status");
        Assert.Equal(" int status", param.SignatureString());
    }

    [Fact]
    public void SignatureString_Existential_ReturnsPublicType()
    {
        var param = new Parameter(new MarshalledType.Existential("ExistentialContainer1", "IMyProtocol"), "handler");
        Assert.Equal(" IMyProtocol handler", param.SignatureString());
    }

    [Fact]
    public void SignatureString_NativeRemappedNonFrozen_ReturnsSafeHandle()
    {
        var param = new Parameter(MarshalledType.NativeRemappedNonFrozen, "url");
        Assert.Equal(" SafeHandle url", param.SignatureString());
    }

    [Fact]
    public void SignatureString_NativeRemappedFrozen_ReturnsSwiftWrapperType()
    {
        var param = new Parameter(new MarshalledType.NativeRemappedFrozen("SwiftData"), "data");
        Assert.Equal(" SwiftData data", param.SignatureString());
    }

    [Fact]
    public void SignatureString_AsyncThrowingContext_ReturnsIntPtr()
    {
        var param = new Parameter(new MarshalledType.AsyncThrowingContext("callback"), "ctx");
        Assert.Equal(" IntPtr ctx", param.SignatureString());
    }

    [Fact]
    public void SignatureString_AsyncThrowingStartFunc_ReturnsDelegateUnmanaged()
    {
        var param = new Parameter(new MarshalledType.AsyncThrowingStartFunc("onStart"), "startFunc");
        Assert.Equal(" delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> startFunc", param.SignatureString());
    }

    [Fact]
    public void SignatureString_CdeclClosureFuncPtr_ReturnsIntPtr()
    {
        var param = new Parameter(new MarshalledType.CdeclClosureFuncPtr("onComplete", "handler"), "funcPtr");
        Assert.Equal(" IntPtr funcPtr", param.SignatureString());
    }

    [Fact]
    public void SignatureString_CdeclClosureContext_ReturnsIntPtr()
    {
        var param = new Parameter(new MarshalledType.CdeclClosureContext("handler"), "context");
        Assert.Equal(" IntPtr context", param.SignatureString());
    }

    [Fact]
    public void SignatureString_PlainType_ReturnsTypeAndName()
    {
        var param = new Parameter(new MarshalledType.Simple("Int64"), "count");
        Assert.Equal(" Int64 count", param.SignatureString());
    }

    [Fact]
    public void SignatureString_WithModifier_IncludesModifier()
    {
        var param = new Parameter(new MarshalledType.Simple("Int64"), "value", "ref");
        Assert.Equal("ref Int64 value", param.SignatureString());
    }

    [Fact]
    public void SignatureString_ConventionCFuncPtr_ReturnsFullType()
    {
        var param = new Parameter(new MarshalledType.ConventionCFuncPtr("delegate* unmanaged[Cdecl]<long, void>"), "callback");
        Assert.Equal(" delegate* unmanaged[Cdecl]<long, void> callback", param.SignatureString());
    }

    [Fact]
    public void SignatureString_NonFrozenSafeHandle_ReturnsSafeHandle()
    {
        var param = new Parameter(MarshalledType.NonFrozenSafeHandle, "loader");
        Assert.Equal(" SafeHandle loader", param.SignatureString());
    }

    [Fact]
    public void SignatureString_FrozenBuffer_ReturnsBufferType()
    {
        var param = new Parameter(new MarshalledType.FrozenBuffer("Point"), "point");
        Assert.Equal(" Point.Buffer point", param.SignatureString());
    }

    [Fact]
    public void SignatureString_SwiftSelfTyped_ReturnsFullType()
    {
        var param = new Parameter(new MarshalledType.SwiftSelfTyped("MyStruct"), "self");
        Assert.Equal(" SwiftSelf<MyStruct> self", param.SignatureString());
    }

    [Fact]
    public void SignatureString_SwiftSelfUntyped_ReturnsSwiftSelf()
    {
        var param = new Parameter(MarshalledType.SwiftSelfUntyped, "self");
        Assert.Equal(" SwiftSelf self", param.SignatureString());
    }

    [Fact]
    public void SignatureString_SwiftClosureLegacy_ReturnsSwiftClosureData()
    {
        var param = new Parameter(MarshalledType.SwiftClosureLegacy, "callback");
        Assert.Equal(" SwiftClosureData callback", param.SignatureString());
    }

    [Fact]
    public void SignatureString_Bool_ReturnsBool()
    {
        var param = new Parameter(MarshalledType.Bool, "flag");
        Assert.Equal(" bool flag", param.SignatureString());
    }

    #endregion

    #region PInvokeSignatureString() Tests

    [Fact]
    public void PInvokeSignatureString_Existential_UsesContainerType()
    {
        var param = new Parameter(new MarshalledType.Existential("ExistentialContainer1", "IMyProtocol"), "handler");
        Assert.Equal(" ExistentialContainer1 handler", param.PInvokeSignatureString());
    }

    [Fact]
    public void PInvokeSignatureString_Bool_AddsMarshalAs()
    {
        var param = new Parameter(MarshalledType.Bool, "flag");
        Assert.Equal("[MarshalAs(UnmanagedType.U1)]  bool flag", param.PInvokeSignatureString());
    }

    [Fact]
    public void PInvokeSignatureString_PlainType_DelegatesToSignatureString()
    {
        var param = new Parameter(new MarshalledType.Simple("Int64"), "count");
        Assert.Equal(param.SignatureString(), param.PInvokeSignatureString());
    }

    [Fact]
    public void PInvokeSignatureString_AsyncCallback_DelegatesToSignatureString()
    {
        var param = new Parameter(MarshalledType.AsyncCallback, "onComplete");
        Assert.Equal(param.SignatureString(), param.PInvokeSignatureString());
    }

    [Fact]
    public void PInvokeSignatureString_SimpleEnum_DelegatesToSignatureString()
    {
        var param = new Parameter(new MarshalledType.SimpleEnum("Int64", "Direction"), "direction");
        Assert.Equal(param.SignatureString(), param.PInvokeSignatureString());
    }

    [Fact]
    public void PInvokeSignatureString_ObjCBridged_DelegatesToSignatureString()
    {
        var param = new Parameter(new MarshalledType.ObjCBridged("UIImage"), "image");
        Assert.Equal(param.SignatureString(), param.PInvokeSignatureString());
    }

    [Fact]
    public void PInvokeSignatureString_WithModifier_Existential()
    {
        var param = new Parameter(new MarshalledType.Existential("ExistentialContainer2", "IProto"), "arg", "ref");
        Assert.Equal("ref ExistentialContainer2 arg", param.PInvokeSignatureString());
    }

    #endregion

    #region ContainsPlaceholder Tests

    [Fact]
    public void ContainsPlaceholder_ParamWithAnyType_ReturnsTrue()
    {
        var anyTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        var sig = new Signature("void", new[] { new Parameter(new MarshalledType.Simple(anyTypeName), "x") });
        Assert.True(sig.ContainsPlaceholder);
    }

    [Fact]
    public void ContainsPlaceholder_ReturnTypeWithAnyType_ReturnsTrue()
    {
        var anyTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        var sig = new Signature(anyTypeName, new[] { new Parameter(new MarshalledType.Simple("int"), "x") });
        Assert.True(sig.ContainsPlaceholder);
    }

    [Fact]
    public void ContainsPlaceholder_NoAnyType_ReturnsFalse()
    {
        var sig = new Signature("void", new[] { new Parameter(new MarshalledType.Simple("int"), "x") });
        Assert.False(sig.ContainsPlaceholder);
    }

    [Fact]
    public void ContainsPlaceholder_AnyTypeInGenericParam_ReturnsTrue()
    {
        var anyTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        var sig = new Signature("void", new[] { new Parameter(new MarshalledType.Simple($"List<{anyTypeName}>"), "items") });
        Assert.True(sig.ContainsPlaceholder);
    }

    [Fact]
    public void ContainsPlaceholder_NonSimpleType_ReturnsFalse()
    {
        // Non-Simple MarshalledType variants should not contain placeholder
        var sig = new Signature("void", new[] { new Parameter(MarshalledType.Bool, "flag") });
        Assert.False(sig.ContainsPlaceholder);
    }

    #endregion

    #region CallString() Tests

    [Fact]
    public void CallString_SimpleType_ReturnsTypeAndName()
    {
        var param = new Parameter(new MarshalledType.Simple("int"), "x");
        Assert.Equal("int x", param.CallString());
    }

    [Fact]
    public void CallString_ExistentialType_ReturnsPublicType()
    {
        var param = new Parameter(new MarshalledType.Existential("ExistentialContainer1", "IProto"), "arg");
        Assert.Equal("IProto arg", param.CallString());
    }

    [Fact]
    public void CallString_SimpleEnum_ReturnsEnumTypeName()
    {
        var param = new Parameter(new MarshalledType.SimpleEnum("Int64", "Direction"), "dir");
        Assert.Equal("Direction dir", param.CallString());
    }

    [Fact]
    public void CallString_Bool_ReturnsBool()
    {
        var param = new Parameter(MarshalledType.Bool, "flag");
        Assert.Equal("bool flag", param.CallString());
    }

    [Fact]
    public void CallString_FrozenBuffer_ReturnsBufferType()
    {
        var param = new Parameter(new MarshalledType.FrozenBuffer("Point"), "p");
        Assert.Equal("Point.Buffer p", param.CallString());
    }

    #endregion
}
