// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests Foundation.URLRequest ObjC bridge projection — verifies URLRequest values cross the
/// @_cdecl boundary as ObjC object pointers (IntPtr via NSURLRequest) instead of Swift struct bytes.
/// Exercises scalar params/returns, optional params/returns, property accessors, and containers.
/// </summary>
public class URLRequestTests : TestBase
{
    public URLRequestTests(TestResults results) : base(results) { }

    #region Scalar Param + Return

    public void TestCreateRequest()
    {
        var url = Foundation.NSUrl.FromString("https://example.com")!;
        using var helper = new SwiftBindingsTestLib.URLRequestTestHelper();
        var request = helper.CreateRequest(url);
        AssertNotNull(request, "CreateRequest returns non-null");
    }

    public void TestGetRequestURL()
    {
        var url = Foundation.NSUrl.FromString("https://example.com/path")!;
        using var helper = new SwiftBindingsTestLib.URLRequestTestHelper();
        var request = helper.CreateRequest(url);
        var resultUrl = helper.GetRequestURL(request);
        AssertNotNull(resultUrl, "GetRequestURL returns non-null");
        AssertEqual("https://example.com/path", resultUrl!.AbsoluteString, "URL preserved through URLRequest round-trip");
    }

    public void TestGetTimeout()
    {
        var url = Foundation.NSUrl.FromString("https://example.com")!;
        using var helper = new SwiftBindingsTestLib.URLRequestTestHelper();
        var request = helper.CreateRequest(url);
        var timeout = helper.GetTimeout(request);
        AssertTrue(timeout > 0, "Timeout is positive (default URLRequest timeout)");
    }

    #endregion

    #region Optional Param + Return

    public void TestAcceptOptionalRequestWithValue()
    {
        var url = Foundation.NSUrl.FromString("https://example.com")!;
        using var helper = new SwiftBindingsTestLib.URLRequestTestHelper();
        var request = helper.CreateRequest(url);
        var result = helper.AcceptOptionalRequest(request);
        AssertTrue(result, "AcceptOptionalRequest returns true for non-null request");
    }

    public void TestAcceptOptionalRequestWithNull()
    {
        using var helper = new SwiftBindingsTestLib.URLRequestTestHelper();
        var result = helper.AcceptOptionalRequest(null);
        AssertFalse(result, "AcceptOptionalRequest returns false for null");
    }

    public void TestGetOptionalRequest()
    {
        var url = Foundation.NSUrl.FromString("https://example.com/optional")!;
        using var helper = new SwiftBindingsTestLib.URLRequestTestHelper();
        var request = helper.GetOptionalRequest(url);
        AssertNotNull(request, "GetOptionalRequest returns non-null");
    }

    #endregion

    #region Property Accessor

    public void TestStoredRequestPropertyGetter()
    {
        using var helper = new SwiftBindingsTestLib.URLRequestTestHelper();
        var request = helper.StoredRequest;
        AssertNotNull(request, "StoredRequest property getter returns non-null");
    }

    public void TestStoredRequestPropertySetter()
    {
        var url = Foundation.NSUrl.FromString("https://setter-test.com")!;
        using var helper = new SwiftBindingsTestLib.URLRequestTestHelper();
        var request = helper.CreateRequest(url);
        helper.StoredRequest = request;
        var result = helper.StoredRequest;
        AssertNotNull(result, "StoredRequest round-trips through property setter/getter");
    }

    #endregion

    #region Container

    public void TestGetRequestArray()
    {
        using var helper = new SwiftBindingsTestLib.URLRequestTestHelper();
        var requests = helper.GetRequestArray();
        AssertNotNull(requests, "GetRequestArray returns non-null");
        AssertEqual(2, requests!.Count, "GetRequestArray returns 2 elements");
    }

    public void TestAcceptRequestArray()
    {
        var url1 = Foundation.NSUrl.FromString("https://one.com")!;
        var url2 = Foundation.NSUrl.FromString("https://two.com")!;
        using var helper = new SwiftBindingsTestLib.URLRequestTestHelper();
        var req1 = helper.CreateRequest(url1);
        var req2 = helper.CreateRequest(url2);
        var requests = new[] { req1, req2 };
        var count = helper.AcceptRequestArray(requests);
        AssertEqual(2, count, "AcceptRequestArray receives correct count");
    }

    #endregion
}
