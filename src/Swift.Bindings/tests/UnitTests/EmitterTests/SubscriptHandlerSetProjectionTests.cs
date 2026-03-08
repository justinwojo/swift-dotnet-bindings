// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SetProjection support in SubscriptHandler accessor conversion methods.
/// Verifies projection parity with PropertyHandler's Set handling.
/// </summary>
public class SubscriptHandlerSetProjectionTests
{
    #region Getter Conversion Tests

    [Fact]
    public void GetSetAccessorGetterConversion_WithElementConversion_ReturnsSelectToHashSet()
    {
        var elem = new StringProjection();
        var set = new SetProjection(elem, isParameter: false);

        var (conversion, requiresDisposal) = SubscriptHandler.GetSetAccessorGetterConversion(set, "result");

        Assert.NotNull(conversion);
        Assert.Contains(".Select(", conversion!);
        Assert.Contains(".ToHashSet()", conversion!);
        Assert.True(requiresDisposal);
    }

    [Fact]
    public void GetSetAccessorGetterConversion_WithoutElementConversion_ReturnsNull()
    {
        var elem = new BlittableProjection("nint");
        var set = new SetProjection(elem, isParameter: false);

        var (conversion, _) = SubscriptHandler.GetSetAccessorGetterConversion(set, "result");

        Assert.Null(conversion);
    }

    #endregion

    #region Setter Conversion Tests

    [Fact]
    public void GetSetAccessorSetterConversion_WithElementConversion_ReturnsFromEnumerableWithSelect()
    {
        var elem = new StringProjection();
        var set = new SetProjection(elem, isParameter: true);

        var (conversion, requiresDisposal) = SubscriptHandler.GetSetAccessorSetterConversion(set, "value");

        Assert.NotNull(conversion);
        Assert.Contains("SwiftSet<", conversion!);
        Assert.Contains(".FromEnumerable(", conversion!);
        Assert.Contains(".Select(", conversion!);
        Assert.True(requiresDisposal);
    }

    [Fact]
    public void GetSetAccessorSetterConversion_WithoutElementConversion_ReturnsFromEnumerableDirect()
    {
        var elem = new BlittableProjection("nint");
        var set = new SetProjection(elem, isParameter: true);

        var (conversion, requiresDisposal) = SubscriptHandler.GetSetAccessorSetterConversion(set, "value");

        Assert.NotNull(conversion);
        Assert.Contains("SwiftSet<", conversion!);
        Assert.Contains(".FromEnumerable(value)", conversion!);
        Assert.DoesNotContain(".Select(", conversion!);
        Assert.True(requiresDisposal);
    }

    [Fact]
    public void GetSetAccessorSetterConversion_ObjCRooted_SkipsElementConversion()
    {
        var elem = new ObjCRootedClassProjection("UIKit.UIView");
        var set = new SetProjection(elem, isParameter: true);

        var (conversion, _) = SubscriptHandler.GetSetAccessorSetterConversion(set, "value");

        Assert.NotNull(conversion);
        Assert.DoesNotContain(".Handle", conversion!);
        Assert.DoesNotContain(".Select", conversion!);
    }

    #endregion

    #region Output Parity with PropertyHandler Tests

    [Fact]
    public void SetSetterConversion_MatchesPropertyHandler()
    {
        // Both PropertyHandler and SubscriptHandler should produce identical output
        // for the same SetProjection input. PropertyHandler's method is internal.
        var elem = new StringProjection();
        var set = new SetProjection(elem, isParameter: true);

        var (subConv, subDisp) = SubscriptHandler.GetSetAccessorSetterConversion(set, "value");
        var (propConv, propDisp) = PropertyHandler.GetSetAccessorSetterConversion(set, "value");

        Assert.Equal(propConv, subConv);
        Assert.Equal(propDisp, subDisp);
    }

    [Fact]
    public void SetSetterConversion_ObjCRooted_MatchesPropertyHandler()
    {
        var elem = new ObjCRootedClassProjection("UIKit.UIView");
        var set = new SetProjection(elem, isParameter: true);

        var (subConv, subDisp) = SubscriptHandler.GetSetAccessorSetterConversion(set, "value");
        var (propConv, propDisp) = PropertyHandler.GetSetAccessorSetterConversion(set, "value");

        Assert.Equal(propConv, subConv);
        Assert.Equal(propDisp, subDisp);
    }

    #endregion
}
