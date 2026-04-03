// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SetProjection and DictionaryProjection support in SubscriptHandler accessor
/// conversion methods. Verifies projection parity with PropertyHandler.
/// </summary>
public class SubscriptHandlerProjectionTests
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

    #region Dictionary Getter AsProjected Overload Tests

    [Fact]
    public void GetDictAccessorGetterConversion_ValueOnly_Uses1ArgAsProjected()
    {
        // When only value needs conversion, should use 1-arg AsProjected(v => ...)
        var key = new BlittableProjection("nint");
        var value = new StringProjection();
        var dict = new DictionaryProjection(key, value, isParameter: false);

        var (conversion, _) = SubscriptHandler.GetDictAccessorGetterConversion(dict, "result");

        Assert.NotNull(conversion);
        // 1-arg overload: AsProjected(v => conversion)
        Assert.Contains(".AsProjected(v =>", conversion!);
        // Must NOT have key selector — would match 3-arg overload which doesn't exist as 2-arg
        Assert.DoesNotContain("k =>", conversion!);
    }

    [Fact]
    public void GetDictAccessorGetterConversion_KeyAndValue_Uses3ArgAsProjected()
    {
        // When key needs conversion, should use 3-arg AsProjected(k => conv, k => reverse, v => conv)
        var key = new StringProjection();
        var value = new StringProjection();
        var dict = new DictionaryProjection(key, value, isParameter: false);

        var (conversion, _) = SubscriptHandler.GetDictAccessorGetterConversion(dict, "result");

        Assert.NotNull(conversion);
        // 3-arg overload includes reverse key conversion
        Assert.Contains(".AsProjected(k =>", conversion!);
    }

    [Fact]
    public void GetDictAccessorGetterConversion_NoConversion_ReturnsNull()
    {
        var key = new BlittableProjection("nint");
        var value = new BlittableProjection("nint");
        var dict = new DictionaryProjection(key, value, isParameter: false);

        var (conversion, _) = SubscriptHandler.GetDictAccessorGetterConversion(dict, "result");

        Assert.Null(conversion);
    }

    #endregion

    #region Top-Level Dispatch Tests

    [Fact]
    public void GetAccessorGetterConversion_SetProjection_DispatchesToSetHelper()
    {
        var elem = new StringProjection();
        var set = new SetProjection(elem, isParameter: false);

        var (conversion, _) = SubscriptHandler.GetAccessorGetterConversion(set, "result");

        Assert.NotNull(conversion);
        Assert.Contains(".Select(", conversion!);
        Assert.Contains(".ToHashSet()", conversion!);
    }

    [Fact]
    public void GetAccessorSetterConversion_SetProjection_DispatchesToSetHelper()
    {
        var elem = new StringProjection();
        var set = new SetProjection(elem, isParameter: true);

        var (conversion, _) = SubscriptHandler.GetAccessorSetterConversion(set, "value");

        Assert.NotNull(conversion);
        Assert.Contains("SwiftSet<", conversion!);
        Assert.Contains(".FromEnumerable(", conversion!);
    }

    #endregion

    #region Optional ObjC Getter Tests

    [Fact]
    public void GetOptionalAccessorGetterConversion_ObjCBridged_ReturnsPointerBridge()
    {
        var inner = new ObjCBridgedProjection("UIKit.UIImage");
        var opt = new OptionalProjection(inner);

        var (conversion, requiresDisposal) = SubscriptHandler.GetOptionalAccessorGetterConversion(opt, "result");

        Assert.NotNull(conversion);
        Assert.Contains("IntPtr.Zero", conversion!);
        Assert.Contains("GetINativeObject", conversion!);
        Assert.False(requiresDisposal);
    }

    [Fact]
    public void GetOptionalAccessorGetterConversion_ObjCBridged_MatchesPropertyHandler()
    {
        var inner = new ObjCBridgedProjection("UIKit.UIImage");
        var opt = new OptionalProjection(inner);

        var (subConv, subDisp) = SubscriptHandler.GetOptionalAccessorGetterConversion(opt, "result");
        var (propConv, propDisp) = PropertyHandler.GetOptionalAccessorGetterConversion(opt, "result");

        Assert.Equal(propConv, subConv);
        Assert.Equal(propDisp, subDisp);
    }

    #endregion

    #region Optional Setter Parity Tests

    [Fact]
    public void GetOptionalAccessorSetterConversion_ObjCBridged_ReturnsHandleOrZero()
    {
        var inner = new ObjCBridgedProjection("UIKit.UIImage");
        var opt = new OptionalProjection(inner);

        var (conversion, requiresDisposal) = SubscriptHandler.GetOptionalAccessorSetterConversion(opt, "value");

        Assert.NotNull(conversion);
        Assert.Contains(".Handle", conversion!);
        Assert.Contains("IntPtr.Zero", conversion!);
        Assert.DoesNotContain("SwiftOptional", conversion!);
        Assert.False(requiresDisposal);
    }

    [Fact]
    public void GetOptionalAccessorSetterConversion_ObjCBridged_MatchesPropertyHandler()
    {
        var inner = new ObjCBridgedProjection("UIKit.UIImage");
        var opt = new OptionalProjection(inner);

        var (subConv, subDisp) = SubscriptHandler.GetOptionalAccessorSetterConversion(opt, "value");
        var (propConv, propDisp) = PropertyHandler.GetOptionalAccessorSetterConversion(opt, "value");

        Assert.Equal(propConv, subConv);
        Assert.Equal(propDisp, subDisp);
    }

    [Fact]
    public void GetOptionalAccessorSetterConversion_ClassInner_UsesSwiftOptionalWrapper()
    {
        var inner = new ClassProjection("TestModule.MyClass");
        var opt = new OptionalProjection(inner);

        var (conversion, requiresDisposal) = SubscriptHandler.GetOptionalAccessorSetterConversion(opt, "value");

        Assert.NotNull(conversion);
        Assert.Contains("SwiftOptional", conversion!);
        Assert.Contains("NewSome", conversion!);
        Assert.Contains("NewNone", conversion!);
        Assert.True(requiresDisposal);
    }

    [Fact]
    public void GetOptionalAccessorSetterConversion_ClassInner_MatchesPropertyHandler()
    {
        var inner = new ClassProjection("TestModule.MyClass");
        var opt = new OptionalProjection(inner);

        var (subConv, subDisp) = SubscriptHandler.GetOptionalAccessorSetterConversion(opt, "value");
        var (propConv, propDisp) = PropertyHandler.GetOptionalAccessorSetterConversion(opt, "value");

        Assert.Equal(propConv, subConv);
        Assert.Equal(propDisp, subDisp);
    }

    [Fact]
    public void GetOptionalAccessorSetterConversion_ObjCRooted_MatchesPropertyHandler()
    {
        var inner = new ObjCRootedClassProjection("UIKit.UIView");
        var opt = new OptionalProjection(inner);

        var (subConv, subDisp) = SubscriptHandler.GetOptionalAccessorSetterConversion(opt, "value");
        var (propConv, propDisp) = PropertyHandler.GetOptionalAccessorSetterConversion(opt, "value");

        Assert.Equal(propConv, subConv);
        Assert.Equal(propDisp, subDisp);
    }

    #endregion

    #region Optional<Set<T>> Tests

    [Fact]
    public void GetOptionalAccessorGetterConversion_OptionalSet_ReturnsDiscriminantCheck()
    {
        var elem = new StringProjection();
        var set = new SetProjection(elem, isParameter: false);
        var opt = new OptionalProjection(set);

        var (conversion, _) = SubscriptHandler.GetOptionalAccessorGetterConversion(opt, "result");

        Assert.NotNull(conversion);
        Assert.Contains("SwiftOptionalCases.None", conversion!);
    }

    [Fact]
    public void GetOptionalAccessorSetterConversion_OptionalSet_WrapsWithSwiftOptional()
    {
        var elem = new StringProjection();
        var set = new SetProjection(elem, isParameter: true);
        var opt = new OptionalProjection(set);

        var (conversion, _) = SubscriptHandler.GetOptionalAccessorSetterConversion(opt, "value");

        Assert.NotNull(conversion);
        Assert.Contains("SwiftOptional", conversion!);
        Assert.Contains("SwiftSet", conversion!);
    }

    #endregion
}
