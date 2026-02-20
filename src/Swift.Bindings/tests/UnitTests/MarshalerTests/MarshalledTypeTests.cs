// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the MarshalledType discriminated union — construction, deconstruction,
/// pattern matching, and helper methods.
/// </summary>
public class MarshalledTypeTests
{
    #region Prefixed Variants

    [Fact]
    public void Existential_ConstructsAndDeconstructs()
    {
        var type = new MarshalledType.Existential("ExistentialContainer1", "IMyProtocol");
        Assert.Equal("ExistentialContainer1", type.ContainerType);
        Assert.Equal("IMyProtocol", type.PublicType);
    }

    [Fact]
    public void SimpleEnum_ConstructsAndDeconstructs()
    {
        var type = new MarshalledType.SimpleEnum("Int64", "Direction");
        Assert.Equal("Int64", type.UnderlyingType);
        Assert.Equal("Direction", type.EnumTypeName);
    }

    [Fact]
    public void ObjCBridged_ConstructsAndDeconstructs()
    {
        var type = new MarshalledType.ObjCBridged("UIImage");
        Assert.Equal("UIImage", type.CSharpTypeName);
    }

    [Fact]
    public void CdeclClosureFuncPtr_ConstructsAndDeconstructs()
    {
        var type = new MarshalledType.CdeclClosureFuncPtr("onComplete", "handler");
        Assert.Equal("onComplete", type.CallbackName);
        Assert.Equal("handler", type.SourceCsName);
    }

    [Fact]
    public void CdeclClosureContext_ConstructsAndDeconstructs()
    {
        var type = new MarshalledType.CdeclClosureContext("handler");
        Assert.Equal("handler", type.SourceCsName);
    }

    [Fact]
    public void AsyncThrowingContext_ConstructsAndDeconstructs()
    {
        var type = new MarshalledType.AsyncThrowingContext("callback");
        Assert.Equal("callback", type.ParamName);
    }

    [Fact]
    public void AsyncThrowingStartFunc_ConstructsAndDeconstructs()
    {
        var type = new MarshalledType.AsyncThrowingStartFunc("onStart");
        Assert.Equal("onStart", type.CallbackName);
    }

    [Fact]
    public void NativeRemappedFrozen_ConstructsAndDeconstructs()
    {
        var type = new MarshalledType.NativeRemappedFrozen("SwiftData");
        Assert.Equal("SwiftData", type.SwiftWrapperType);
    }

    [Fact]
    public void FrozenBuffer_ConstructsAndDeconstructs()
    {
        var type = new MarshalledType.FrozenBuffer("Point");
        Assert.Equal("Point", type.TypeName);
    }

    [Fact]
    public void ConventionCFuncPtr_ConstructsAndDeconstructs()
    {
        var type = new MarshalledType.ConventionCFuncPtr("delegate* unmanaged[Cdecl]<long, void>");
        Assert.Equal("delegate* unmanaged[Cdecl]<long, void>", type.FuncPtrType);
    }

    [Fact]
    public void SwiftSelfTyped_ConstructsAndDeconstructs()
    {
        var type = new MarshalledType.SwiftSelfTyped("MyStruct");
        Assert.Equal("MyStruct", type.InnerType);
    }

    [Fact]
    public void Simple_ConstructsAndDeconstructs()
    {
        var type = new MarshalledType.Simple("Int64");
        Assert.Equal("Int64", type.CSharpType);
    }

    #endregion

    #region Singleton Variants

    [Fact]
    public void AsyncCallback_IsSingleton()
    {
        Assert.Same(MarshalledType.AsyncCallback, MarshalledType.AsyncCallback);
        Assert.IsType<MarshalledType.AsyncCallbackType>(MarshalledType.AsyncCallback);
    }

    [Fact]
    public void AsyncErrorCallback_IsSingleton()
    {
        Assert.Same(MarshalledType.AsyncErrorCallback, MarshalledType.AsyncErrorCallback);
    }

    [Fact]
    public void AsyncContext_IsSingleton()
    {
        Assert.Same(MarshalledType.AsyncContext, MarshalledType.AsyncContext);
    }

    [Fact]
    public void AsyncTask_IsSingleton()
    {
        Assert.Same(MarshalledType.AsyncTask, MarshalledType.AsyncTask);
    }

    [Fact]
    public void NonFrozenIntPtr_IsSingleton()
    {
        Assert.Same(MarshalledType.NonFrozenIntPtr, MarshalledType.NonFrozenIntPtr);
    }

    [Fact]
    public void EnumSafeHandle_IsSingleton()
    {
        Assert.Same(MarshalledType.EnumSafeHandle, MarshalledType.EnumSafeHandle);
    }

    [Fact]
    public void NativeRemappedNonFrozen_IsSingleton()
    {
        Assert.Same(MarshalledType.NativeRemappedNonFrozen, MarshalledType.NativeRemappedNonFrozen);
    }

    [Fact]
    public void NonFrozenSafeHandle_IsSingleton()
    {
        Assert.Same(MarshalledType.NonFrozenSafeHandle, MarshalledType.NonFrozenSafeHandle);
    }

    [Fact]
    public void SwiftClosureLegacy_IsSingleton()
    {
        Assert.Same(MarshalledType.SwiftClosureLegacy, MarshalledType.SwiftClosureLegacy);
    }

    [Fact]
    public void Bool_IsSingleton()
    {
        Assert.Same(MarshalledType.Bool, MarshalledType.Bool);
    }

    [Fact]
    public void SwiftSelfUntyped_IsSingleton()
    {
        Assert.Same(MarshalledType.SwiftSelfUntyped, MarshalledType.SwiftSelfUntyped);
    }

    #endregion

    #region Pattern Matching

    [Fact]
    public void PatternMatch_Existential()
    {
        MarshalledType type = new MarshalledType.Existential("Container", "IProto");
        Assert.True(type is MarshalledType.Existential(var c, var p) && c == "Container" && p == "IProto");
    }

    [Fact]
    public void PatternMatch_SimpleEnum()
    {
        MarshalledType type = new MarshalledType.SimpleEnum("int", "Status");
        Assert.True(type is MarshalledType.SimpleEnum(var u, var e) && u == "int" && e == "Status");
    }

    [Fact]
    public void PatternMatch_Singleton()
    {
        MarshalledType type = MarshalledType.Bool;
        Assert.True(type is MarshalledType.BoolType);
    }

    [Fact]
    public void PatternMatch_Simple()
    {
        MarshalledType type = new MarshalledType.Simple("MyType");
        Assert.True(type is MarshalledType.Simple("MyType"));
    }

    #endregion

    #region ContainsAnyTypePlaceholder

    [Fact]
    public void ContainsAnyTypePlaceholder_SimpleWithAnyType_ReturnsTrue()
    {
        var anyTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        var type = new MarshalledType.Simple(anyTypeName);
        Assert.True(type.ContainsAnyTypePlaceholder());
    }

    [Fact]
    public void ContainsAnyTypePlaceholder_SimpleWithGenericAnyType_ReturnsTrue()
    {
        var anyTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        var type = new MarshalledType.Simple($"List<{anyTypeName}>");
        Assert.True(type.ContainsAnyTypePlaceholder());
    }

    [Fact]
    public void ContainsAnyTypePlaceholder_SimpleNormal_ReturnsFalse()
    {
        var type = new MarshalledType.Simple("int");
        Assert.False(type.ContainsAnyTypePlaceholder());
    }

    [Fact]
    public void ContainsAnyTypePlaceholder_NonSimple_ReturnsFalse()
    {
        MarshalledType type = MarshalledType.Bool;
        Assert.False(type.ContainsAnyTypePlaceholder());
    }

    #endregion

    #region PublicTypeName

    [Fact]
    public void PublicTypeName_Simple_ReturnsInnerType()
    {
        Assert.Equal("int", new MarshalledType.Simple("int").PublicTypeName);
    }

    [Fact]
    public void PublicTypeName_Existential_ReturnsPublicType()
    {
        Assert.Equal("IProto", new MarshalledType.Existential("Container", "IProto").PublicTypeName);
    }

    [Fact]
    public void PublicTypeName_SimpleEnum_ReturnsEnumName()
    {
        Assert.Equal("Direction", new MarshalledType.SimpleEnum("int", "Direction").PublicTypeName);
    }

    [Fact]
    public void PublicTypeName_Bool_ReturnsBool()
    {
        Assert.Equal("bool", MarshalledType.Bool.PublicTypeName);
    }

    [Fact]
    public void PublicTypeName_FrozenBuffer_ReturnsBufferType()
    {
        Assert.Equal("Point.Buffer", new MarshalledType.FrozenBuffer("Point").PublicTypeName);
    }

    #endregion

    #region Record Equality

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new MarshalledType.Existential("C1", "IProto");
        var b = new MarshalledType.Existential("C1", "IProto");
        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentValues_AreNotEqual()
    {
        var a = new MarshalledType.Existential("C1", "IProto");
        var b = new MarshalledType.Existential("C2", "IProto");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentVariants_AreNotEqual()
    {
        MarshalledType a = new MarshalledType.Simple("bool");
        MarshalledType b = MarshalledType.Bool;
        Assert.NotEqual(a, b);
    }

    #endregion
}
