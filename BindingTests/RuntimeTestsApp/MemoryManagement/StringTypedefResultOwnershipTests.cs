// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Ownership of NSString-typedef results (RunLoop.Mode, UIFont.TextStyle, UIContentSizeCategory),
/// which the binding carries as <see cref="NSString"/>.
///
/// Swift returns them <c>@owned</c>, so every route hands the string back at +1: a native thunk
/// forwards Swift's +1, a <c>@_cdecl</c> wrapper that returns through a result buffer leaves the +1
/// in the buffer. The managed side must adopt it exactly once. Each echo below returns the object
/// the test passed in, so its retain count before and after a run of calls shows a leaked retain
/// (it grows by one per call) or an extra release (it shrinks, and the string is eventually freed
/// under its owner). A fresh string Swift builds must end up owned by its managed wrapper alone.
///
/// The text is long and non-ASCII so Foundation stores it on the heap rather than as a tagged
/// pointer, whose retain count means nothing.
/// </summary>
public class StringTypedefResultOwnershipTests : TestBase
{
    public StringTypedefResultOwnershipTests(TestResults results) : base(results) { }

    private const string Text = "StringTypedefResultOwnershipTests-mode-ü-0123456789";
    private const int Calls = 16;

    private void AssertEchoKeepsRetainCount(string route, Func<NSString, NSString> echo)
    {
        using var mode = new NSString(Text);
        var before = mode.RetainCount;
        for (var i = 0; i < Calls; i++)
        {
            var echoed = echo(mode);
            AssertEqual(mode.Handle, echoed.Handle, $"{route}: Swift returns the object it was given");
            AssertEqual(Text, echoed.ToString(), $"{route}: the returned string reads back");
        }
        var after = mode.RetainCount;
        TestLogger.Info($"{route}: retain count {before} before {Calls} calls, {after} after");
        AssertEqual(before, after, $"{route}: the result's +1 is adopted exactly once per call");
    }

    public void TestNativeThunkMethod_EchoAdoptsResult()
    {
        using var holder = new StringTypedefOwnershipHolder();
        AssertEchoKeepsRetainCount("thunk method", m => holder.EchoMode(m));
    }

    public void TestNativeThunkStaticMethod_EchoAdoptsResult()
        => AssertEchoKeepsRetainCount("thunk static method", StringTypedefOwnershipHolder.EchoModeStatic);

    public void TestClassMethod_EchoAdoptsResult()
    {
        using var box = new StringTypedefOwnershipBox();
        AssertEchoKeepsRetainCount("class method", m => box.EchoMode(m));
    }

    public void TestFreeFunction_EchoAdoptsResult()
        => AssertEchoKeepsRetainCount("free function", TestLibFunctions.StringTypedefOwnershipEchoMode);

    /// <summary>
    /// The String parameter sends this member through a <c>@_cdecl</c> wrapper that returns the mode
    /// through a result buffer, so the managed side has to read the object out of the buffer rather
    /// than treat the buffer's address as the object.
    /// </summary>
    public void TestResultBufferWrapper_EchoReadsAndAdoptsTheObject()
    {
        using var holder = new StringTypedefOwnershipHolder();
        AssertEchoKeepsRetainCount("result-buffer wrapper", m => holder.EchoMode(m, "tag"));
    }

    public void TestGenericReceiverWrapper_EchoReadsAndAdoptsTheObject()
    {
        using var box = new StringTypedefOwnershipGenericBox<long>(3);
        AssertEchoKeepsRetainCount("generic receiver wrapper", m => box.EchoMode(m));
    }

    public void TestResultBufferWrapper_FreshStringIsOwnedByItsWrapperAlone()
    {
        using var holder = new StringTypedefOwnershipHolder();
        using var fresh = holder.FreshMode("fresh");
        AssertEqual("StringTypedefOwnership-fresh-ü", fresh.ToString(), "the fresh string reads back");
        TestLogger.Info($"fresh string retain count: {fresh.RetainCount}");
        AssertEqual((nuint)1, fresh.RetainCount, "only the managed wrapper owns a string Swift built and returned");
    }

#if UIKIT_RAW_VALUES
    public void TestTextStyleAndCategory_EchoAdoptsResult()
    {
        using var holder = new StringTypedefOwnershipHolder();
        AssertEchoKeepsRetainCount("text style", m => holder.EchoTextStyle(m));
        AssertEchoKeepsRetainCount("content size category", m => holder.EchoCategory(m));
        AssertEchoKeepsRetainCount("free text style", TestLibFunctions.StringTypedefOwnershipEchoTextStyle);
    }
#endif
}
