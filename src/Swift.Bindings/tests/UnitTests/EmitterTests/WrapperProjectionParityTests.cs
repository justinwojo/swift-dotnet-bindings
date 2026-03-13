// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Verifies that the shared visitor classes (AccessorGetterConversionVisitor,
/// AccessorSetterConversionVisitor) produce correct output for each projection type.
/// These visitors replaced duplicated switch patterns in PropertyHandler and SubscriptHandler.
/// </summary>
public class WrapperProjectionParityTests
{
    #region Getter Visitor — Conversion Cases

    [Fact]
    public void GetterVisitor_String_ReturnsToString()
    {
        var proj = new StringProjection();
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.Equal("result.ToString()", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void GetterVisitor_Data_ReturnsToByteArray()
    {
        var proj = new DataProjection();
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.Equal("result.ToByteArray()", conversion);
        Assert.False(disposal);
    }

    [Fact]
    public void GetterVisitor_NativeRemapped_ReturnsConversionMethod()
    {
        var proj = new NativeRemappedProjection("NSUrl", "Swift.URL", isFrozen: true,
            toConversionMethod: "ToNSUrl", requiresDisposal: true);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.Equal("result.ToNSUrl()", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void GetterVisitor_ArrayWithStringElement_ReturnsAsProjected()
    {
        var elem = new StringProjection();
        var proj = new ArrayProjection(elem, isParameter: false);
        var (conversion, _) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.NotNull(conversion);
        Assert.Contains("AsProjected", conversion);
    }

    [Fact]
    public void GetterVisitor_ArrayWithBlittableElement_ReturnsNull()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: false);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.Null(conversion);
        Assert.False(disposal);
    }

    [Fact]
    public void GetterVisitor_DictionaryWithStringValue_ReturnsAsProjected()
    {
        var key = new BlittableProjection("Int64");
        var val = new StringProjection();
        var proj = new DictionaryProjection(key, val, isParameter: false);
        var (conversion, _) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.NotNull(conversion);
        Assert.Contains("AsProjected", conversion);
    }

    [Fact]
    public void GetterVisitor_SetWithStringElement_ReturnsSelectToHashSet()
    {
        var elem = new StringProjection();
        var proj = new SetProjection(elem, isParameter: false);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.NotNull(conversion);
        Assert.Contains("Select", conversion);
        Assert.Contains("ToHashSet", conversion);
        Assert.True(disposal);
    }

    #endregion

    #region Getter Visitor — Passthrough Cases

    [Theory]
    [MemberData(nameof(PassthroughProjections))]
    public void GetterVisitor_Passthrough_ReturnsNull(ITypeProjection proj)
    {
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.Null(conversion);
        Assert.False(disposal);
    }

    #endregion

    #region Setter Visitor — Conversion Cases

    [Fact]
    public void SetterVisitor_String_ReturnsNewSwiftString()
    {
        var proj = new StringProjection();
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Equal("new SwiftString(value)", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void SetterVisitor_Data_ReturnsFromByteArray()
    {
        var proj = new DataProjection();
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Equal("Swift.Data.FromByteArray(value)", conversion);
        Assert.False(disposal);
    }

    [Fact]
    public void SetterVisitor_NativeRemapped_WithFactory_ReturnsFactoryCall()
    {
        var proj = new NativeRemappedProjection("NSUrl", "Swift.URL", isFrozen: true,
            toConversionMethod: "ToNSUrl", fromFactoryMethod: "FromNSUrl");
        var (conversion, _) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Equal("Swift.URL.FromNSUrl(value)", conversion);
    }

    [Fact]
    public void SetterVisitor_NativeRemapped_NoFactory_ReturnsConstructor()
    {
        var proj = new NativeRemappedProjection("NSUrl", "Swift.URL", isFrozen: true,
            toConversionMethod: "ToNSUrl");
        var (conversion, _) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Equal("new Swift.URL(value)", conversion);
    }

    [Fact]
    public void SetterVisitor_ArrayOfStrings_ReturnsFromEnumerable()
    {
        var elem = new StringProjection();
        var proj = new ArrayProjection(elem, isParameter: false);
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.NotNull(conversion);
        Assert.Contains("SwiftArray", conversion);
        Assert.Contains("FromEnumerable", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void SetterVisitor_ArrayOfBlittable_ReturnsFromEnumerableNoConversion()
    {
        var elem = new BlittableProjection("Int64");
        var proj = new ArrayProjection(elem, isParameter: false);
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.NotNull(conversion);
        Assert.Contains("FromEnumerable(value)", conversion);
        Assert.True(disposal);
    }

    #endregion

    #region Setter Visitor — Passthrough Cases

    [Theory]
    [MemberData(nameof(PassthroughProjections))]
    public void SetterVisitor_Passthrough_ReturnsNull(ITypeProjection proj)
    {
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Null(conversion);
        Assert.False(disposal);
    }

    #endregion

    #region Optional Getter Visitor

    [Fact]
    public void OptionalGetter_StringInner_ReturnsCastWithToString()
    {
        var inner = new StringProjection();
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.NotNull(conversion);
        Assert.Contains("ToString", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void OptionalGetter_BlittableInner_ReturnsNullableCast()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.NotNull(conversion);
        Assert.Contains("Int64?", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void OptionalGetter_ClassInner_ReturnsNull()
    {
        var inner = new ClassProjection("MyClass");
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        // Class optionals are passthrough — accessor already returns nullable
        Assert.Null(conversion);
        Assert.False(disposal);
    }

    [Fact]
    public void OptionalGetter_ExistentialInner_ReturnsNull()
    {
        var inner = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IMyProtocol", "MyProtocolProxy");
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorGetterConversionVisitor("result"));

        Assert.Null(conversion);
        Assert.False(disposal);
    }

    #endregion

    #region Optional Setter Visitor

    [Fact]
    public void OptionalSetter_StringInner_ReturnsSwiftOptionalWrapping()
    {
        var inner = new StringProjection();
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.NotNull(conversion);
        Assert.Contains("SwiftOptional", conversion);
        Assert.Contains("NewSome", conversion);
        Assert.Contains("NewNone", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void OptionalSetter_BlittableInner_ReturnsSwiftOptionalWrapping()
    {
        var inner = new BlittableProjection("Int64");
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.NotNull(conversion);
        Assert.Contains("SwiftOptional", conversion);
        Assert.True(disposal);
    }

    [Fact]
    public void OptionalSetter_ClosureInner_ReturnsNull()
    {
        var inner = new ClosureProjection(Array.Empty<ITypeProjection>(), null, isEscaping: true, throws: false, isAsync: false, callbackName: "cb");
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Null(conversion);
        Assert.False(disposal);
    }

    [Fact]
    public void OptionalSetter_ExistentialInner_ReturnsNull()
    {
        var inner = new ExistentialProjection(
            "Swift.Runtime.ExistentialContainer1", "IMyProtocol", "MyProtocolProxy");
        var proj = new OptionalProjection(inner);
        var (conversion, disposal) = proj.Accept(new AccessorSetterConversionVisitor("value"));

        Assert.Null(conversion);
        Assert.False(disposal);
    }

    #endregion

    #region Shared Test Data

    public static TheoryData<ITypeProjection> PassthroughProjections => new()
    {
        new BlittableProjection("Int64"),
        new BoolProjection(),
        new SimpleEnumProjection("Direction", "int"),
        new ClassProjection("MyClass"),
        new NonFrozenStructProjection("MyStruct"),
        new ExistentialProjection("Swift.Runtime.ExistentialContainer1", "IMyProtocol", "MyProtocolProxy"),
        new ClosureProjection(Array.Empty<ITypeProjection>(), null, isEscaping: true, throws: false, isAsync: false, callbackName: "cb"),
    };

    #endregion
}
