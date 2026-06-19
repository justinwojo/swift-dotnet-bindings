// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the per-projection dispatch of the receiver-conversion visitors that replaced the
/// <c>_ => null</c>/<c>_ => false</c> switches in <c>ProtocolProxyEmitter.Receivers.cs</c>
/// (AF05 Target A). The owner-delegating arms (Array/Dictionary/Set/Optional getter+setter)
/// route to the emitter's private helpers and are covered end-to-end by the byte-identical
/// generated-output bake; here we assert the self-contained arms — the literal conversion
/// expressions, the reference-backed copy-out shapes, and the collection passthrough flag —
/// plus a representative set of passthrough arms that must keep returning null.
/// The owner is only dereferenced by the collection/optional arms, so the self-contained
/// arms are exercised with a null owner.
/// </summary>
public class ReceiverConversionVisitorTests
{
    private const string Marshal = "global::Swift.Runtime.InteropServices.SwiftMarshal";

    private static ProtocolProxyEmitter.ReceiverGetterConversionVisitor Getter(string varName) =>
        new(varName, null!);

    private static ProtocolProxyEmitter.ReceiverSetterConversionVisitor Setter(string varName) =>
        new(varName, null!);

    #region Getter — active arms (C# idiomatic value → Swift ABI carrier)

    [Fact]
    public void Getter_String_WrapsInSwiftString()
    {
        var result = new StringProjection().Accept(Getter("v"));
        Assert.Equal("new SwiftString(v)", result);
    }

    [Fact]
    public void Getter_Data_UsesFromByteArray()
    {
        var result = new DataProjection().Accept(Getter("v"));
        Assert.Equal("Swift.Foundation.Data.FromByteArray(v)", result);
    }

    [Fact]
    public void Getter_Date_OffsetsFromSwiftEpoch()
    {
        var result = new DateProjection().Accept(Getter("v"));
        Assert.Equal($"(v - {DateProjection.SwiftEpoch}).TotalSeconds", result);
    }

    [Fact]
    public void Getter_NativeRemapped_WithFactory_UsesFactoryMethod()
    {
        var proj = new NativeRemappedProjection("NSUrl", "Swift.Foundation.Data", isFrozen: true,
            toConversionMethod: "ToNSUrl", fromFactoryMethod: "FromNSUrl");
        var result = proj.Accept(Getter("v"));
        Assert.Equal("Swift.Foundation.Data.FromNSUrl(v)", result);
    }

    [Fact]
    public void Getter_NativeRemapped_NoFactory_UsesConstructor()
    {
        var proj = new NativeRemappedProjection("NSUrl", "Swift.Foundation.Data", isFrozen: true,
            toConversionMethod: "ToNSUrl");
        var result = proj.Accept(Getter("v"));
        Assert.Equal("new Swift.Foundation.Data(v)", result);
    }

    [Fact]
    public void Getter_ObjCBridged_PassesHandle()
    {
        var result = new ObjCBridgedProjection("Foundation.NSUrl").Accept(Getter("v"));
        Assert.Equal("v.Handle", result);
    }

    [Fact]
    public void Getter_ObjCBridgeable_PassesHandle()
    {
        var result = new ObjCBridgeableProjection("Foundation.NSUrl").Accept(Getter("v"));
        Assert.Equal("v.Handle", result);
    }

    [Fact]
    public void Getter_ObjCRootedClass_PassesHandle()
    {
        var result = new ObjCRootedClassProjection("MyNSObject").Accept(Getter("v"));
        Assert.Equal("v.Handle", result);
    }

    #endregion

    #region Getter — passthrough arms (no whole-value conversion)

    [Fact]
    public void Getter_Class_IsPassthrough()
    {
        Assert.Null(new ClassProjection("MyClass").Accept(Getter("v")));
    }

    [Fact]
    public void Getter_Blittable_IsPassthrough()
    {
        Assert.Null(new BlittableProjection("Int64").Accept(Getter("v")));
    }

    [Fact]
    public void Getter_Bool_IsPassthrough()
    {
        Assert.Null(new BoolProjection().Accept(Getter("v")));
    }

    #endregion

    #region Setter — active arms (Swift ABI value → C# idiomatic form)

    [Fact]
    public void Setter_String_CallsToString()
    {
        var result = new StringProjection().Accept(Setter("v"));
        Assert.Equal("v.ToString()", result);
    }

    [Fact]
    public void Setter_Data_CallsToByteArray()
    {
        var result = new DataProjection().Accept(Setter("v"));
        Assert.Equal("v.ToByteArray()", result);
    }

    [Fact]
    public void Setter_Date_AddsSecondsToSwiftEpoch()
    {
        var result = new DateProjection().Accept(Setter("v"));
        Assert.Equal($"{DateProjection.SwiftEpoch}.AddSeconds(v)", result);
    }

    [Fact]
    public void Setter_NativeRemapped_CallsToConversionMethod()
    {
        var proj = new NativeRemappedProjection("NSUrl", "Swift.Foundation.Data", isFrozen: true,
            toConversionMethod: "ToNSUrl", fromFactoryMethod: "FromNSUrl");
        var result = proj.Accept(Setter("v"));
        Assert.Equal("v.ToNSUrl()", result);
    }

    [Fact]
    public void Setter_ObjCBridged_FormatsBridgeCall()
    {
        var result = new ObjCBridgedProjection("Foundation.NSUrl").Accept(Setter("v"));
        Assert.Equal(MarshallingHelpers.FormatObjCBridgeCall("Foundation.NSUrl", "v", nonNull: true), result);
    }

    [Fact]
    public void Setter_ObjCBridgeable_FormatsBridgeCall()
    {
        var result = new ObjCBridgeableProjection("Foundation.NSUrl").Accept(Setter("v"));
        Assert.Equal(MarshallingHelpers.FormatObjCBridgeCall("Foundation.NSUrl", "v", nonNull: true), result);
    }

    [Fact]
    public void Setter_ObjCRootedClass_IsPassthrough()
    {
        // ObjCRootedClass setter is passthrough (the handle round-trips directly).
        Assert.Null(new ObjCRootedClassProjection("MyNSObject").Accept(Setter("v")));
    }

    [Fact]
    public void Setter_Class_IsPassthrough()
    {
        Assert.Null(new ClassProjection("MyClass").Accept(Setter("v")));
    }

    #endregion

    #region ClassCopyOut — reference-backed copy-out from an ABI slot

    [Fact]
    public void ClassCopyOut_Class_MarshalsBorrowedFromSlot()
    {
        var result = new ClassProjection("MyClass").Accept(
            new ProtocolProxyEmitter.ReceiverClassCopyOutVisitor("slot"));
        Assert.Equal($"{Marshal}.MarshalBorrowedClassFromSlot<MyClass>(slot)", result);
    }

    [Fact]
    public void ClassCopyOut_ObjCRootedClass_MarshalsBorrowedFromSlot()
    {
        var result = new ObjCRootedClassProjection("MyNSObject").Accept(
            new ProtocolProxyEmitter.ReceiverClassCopyOutVisitor("slot"));
        Assert.Equal($"{Marshal}.MarshalBorrowedClassFromSlot<MyNSObject>(slot)", result);
    }

    [Fact]
    public void ClassCopyOut_OptionalClass_MarshalsBorrowedOptionalFromSlot()
    {
        var proj = new OptionalProjection(new ClassProjection("MyClass"));
        var result = proj.Accept(new ProtocolProxyEmitter.ReceiverClassCopyOutVisitor("slot"));
        Assert.Equal($"{Marshal}.MarshalBorrowedOptionalClassFromSlot<MyClass>(slot)", result);
    }

    [Fact]
    public void ClassCopyOut_OptionalObjCRootedClass_MarshalsBorrowedOptionalFromSlot()
    {
        var proj = new OptionalProjection(new ObjCRootedClassProjection("MyNSObject"));
        var result = proj.Accept(new ProtocolProxyEmitter.ReceiverClassCopyOutVisitor("slot"));
        Assert.Equal($"{Marshal}.MarshalBorrowedOptionalClassFromSlot<MyNSObject>(slot)", result);
    }

    [Fact]
    public void ClassCopyOut_OptionalNonClass_IsNull()
    {
        // Optional wrapping a non-class inner kind is not a reference-backed copy-out.
        var proj = new OptionalProjection(new StringProjection());
        var result = proj.Accept(new ProtocolProxyEmitter.ReceiverClassCopyOutVisitor("slot"));
        Assert.Null(result);
    }

    [Fact]
    public void ClassCopyOut_NonClass_IsNull()
    {
        var result = new StringProjection().Accept(
            new ProtocolProxyEmitter.ReceiverClassCopyOutVisitor("slot"));
        Assert.Null(result);
    }

    #endregion

    #region NeedsObjectMarshal — collection wrappers materialize via NewFromPayload

    [Fact]
    public void NeedsObjectMarshal_Array_IsTrue()
    {
        var proj = new ArrayProjection(new BlittableProjection("Int64"), isParameter: true);
        Assert.True(proj.Accept(new ProtocolProxyEmitter.ReceiverParamNeedsObjectMarshalVisitor()));
    }

    [Fact]
    public void NeedsObjectMarshal_Dictionary_IsTrue()
    {
        var proj = new DictionaryProjection(
            new BlittableProjection("Int64"), new BlittableProjection("Int64"), isParameter: true);
        Assert.True(proj.Accept(new ProtocolProxyEmitter.ReceiverParamNeedsObjectMarshalVisitor()));
    }

    [Fact]
    public void NeedsObjectMarshal_Set_IsTrue()
    {
        var proj = new SetProjection(new BlittableProjection("Int64"), isParameter: true);
        Assert.True(proj.Accept(new ProtocolProxyEmitter.ReceiverParamNeedsObjectMarshalVisitor()));
    }

    [Fact]
    public void NeedsObjectMarshal_String_IsFalse()
    {
        Assert.False(new StringProjection().Accept(
            new ProtocolProxyEmitter.ReceiverParamNeedsObjectMarshalVisitor()));
    }

    [Fact]
    public void NeedsObjectMarshal_Class_IsFalse()
    {
        Assert.False(new ClassProjection("MyClass").Accept(
            new ProtocolProxyEmitter.ReceiverParamNeedsObjectMarshalVisitor()));
    }

    [Fact]
    public void NeedsObjectMarshal_Optional_IsFalse()
    {
        // An Optional wrapping a collection is still false at the top level — the discriminator
        // is the top-level projection KIND, not the inner element.
        var proj = new OptionalProjection(new ArrayProjection(new BlittableProjection("Int64"), isParameter: true));
        Assert.False(proj.Accept(new ProtocolProxyEmitter.ReceiverParamNeedsObjectMarshalVisitor()));
    }

    #endregion
}
