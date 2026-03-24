// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests Foundation.URL ObjC bridge projection — verifies URL values cross the
/// @_cdecl boundary as ObjC object pointers (IntPtr) instead of Swift struct bytes.
/// Exercises scalar params/returns, optional params/returns, and property accessors.
/// </summary>
public class URLBridgeTests : TestBase
{
    public URLBridgeTests(TestResults results) : base(results) { }

    #region Construction

    public void TestConstructWithURL()
    {
        var url = Foundation.NSUrl.FromString("https://example.com")!;
        using var helper = new SwiftBindingsTestLib.URLTestHelper(url);
        AssertNotNull(helper, "URLTestHelper constructed with URL");
    }

    #endregion

    #region Scalar Param + Return

    public void TestGetURL()
    {
        var url = Foundation.NSUrl.FromString("https://example.com/path")!;
        using var helper = new SwiftBindingsTestLib.URLTestHelper(url);
        var result = helper.GetURL();
        AssertNotNull(result, "GetURL returns non-null");
        AssertEqual("https://example.com/path", result!.AbsoluteString, "GetURL preserves URL string");
    }

    public void TestSetURL()
    {
        var url1 = Foundation.NSUrl.FromString("https://example.com")!;
        using var helper = new SwiftBindingsTestLib.URLTestHelper(url1);

        var url2 = Foundation.NSUrl.FromString("https://changed.com")!;
        helper.SetURL(url2);

        var result = helper.GetURL();
        AssertEqual("https://changed.com", result!.AbsoluteString, "SetURL changes stored URL");
    }

    #endregion

    #region Optional Param + Return

    public void TestGetOptionalURL()
    {
        var url = Foundation.NSUrl.FromString("https://example.com/optional")!;
        using var helper = new SwiftBindingsTestLib.URLTestHelper(url);
        var result = helper.GetOptionalURL();
        AssertNotNull(result, "GetOptionalURL returns non-null for stored URL");
        AssertEqual("https://example.com/optional", result!.AbsoluteString, "GetOptionalURL preserves URL");
    }

    public void TestAcceptOptionalURLWithValue()
    {
        var url1 = Foundation.NSUrl.FromString("https://example.com")!;
        using var helper = new SwiftBindingsTestLib.URLTestHelper(url1);

        var url2 = Foundation.NSUrl.FromString("https://updated.com")!;
        var accepted = helper.AcceptOptionalURL(url2);
        AssertTrue(accepted, "AcceptOptionalURL returns true for non-null");
        AssertEqual("https://updated.com", helper.GetURL()!.AbsoluteString, "URL updated after accept");
    }

    public void TestAcceptOptionalURLWithNull()
    {
        var url = Foundation.NSUrl.FromString("https://example.com")!;
        using var helper = new SwiftBindingsTestLib.URLTestHelper(url);

        var accepted = helper.AcceptOptionalURL(null);
        AssertFalse(accepted, "AcceptOptionalURL returns false for null");
        AssertEqual("https://example.com", helper.GetURL()!.AbsoluteString, "URL unchanged after null accept");
    }

    #endregion

    #region Property Accessors

    public void TestURLPropertyGetter()
    {
        var url = Foundation.NSUrl.FromString("https://example.com/prop")!;
        using var helper = new SwiftBindingsTestLib.URLTestHelper(url);
        var result = helper.Url;
        AssertNotNull(result, "URL property getter returns non-null");
        AssertEqual("https://example.com/prop", result!.AbsoluteString, "URL property getter preserves URL");
    }

    public void TestURLPropertySetter()
    {
        var url1 = Foundation.NSUrl.FromString("https://example.com")!;
        using var helper = new SwiftBindingsTestLib.URLTestHelper(url1);

        var url2 = Foundation.NSUrl.FromString("https://setter.com")!;
        helper.Url = url2;

        var result = helper.Url;
        AssertEqual("https://setter.com", result!.AbsoluteString, "URL property setter updates URL");
    }

    public void TestOptionalURLPropertyGetter()
    {
        var url = Foundation.NSUrl.FromString("https://example.com/optprop")!;
        using var helper = new SwiftBindingsTestLib.URLTestHelper(url);
        var result = helper.OptionalURL;
        AssertNotNull(result, "Optional URL property returns non-null");
        AssertEqual("https://example.com/optprop", result!.AbsoluteString, "Optional URL property preserves URL");
    }

    #endregion
}
